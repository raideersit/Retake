using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ReplayClip.Core;

/// <summary>
/// Averigua a qué juego se estaba jugando. Cada segundo anota qué programa tiene el primer plano; al guardar un
/// clip se elige el juego al que pertenecieron más segundos del tramo grabado (así sobrevive a un alt-tab corto).
/// Un programa cuenta como juego si está en la tabla de juegos conocidos, en los alias del usuario o si vive en
/// una carpeta de lanzador de juegos (Steam, Epic, Riot, GOG...). Solo consulta user32/kernel32: sin hooks.
/// </summary>
public sealed class GameDetector : IDisposable
{
    private const int MaxSamples = 600;
    private const int MinSecondsToName = 2;

    /// <summary>Siglas de juegos conocidos (nombre del proceso sin .exe).</summary>
    private static readonly Dictionary<string, string> KnownGames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["league of legends"] = "LOL", ["leagueclientux"] = "LOL", ["leagueclient"] = "LOL",
        ["wdc"] = "TWD", ["walkingdead101"] = "TWD", ["walkingdead201"] = "TWD", ["walkingdead301"] = "TWD", ["walkingdead401"] = "TWD",
        ["valorant-win64-shipping"] = "VALORANT", ["cs2"] = "CS2", ["csgo"] = "CSGO", ["dota2"] = "DOTA2",
        ["fortniteclient-win64-shipping"] = "FORTNITE", ["gta5"] = "GTAV", ["eldenring"] = "ELDENRING",
        ["r5apex"] = "APEX", ["overwatch"] = "OVERWATCH", ["rocketleague"] = "ROCKETLEAGUE",
        ["robloxplayerbeta"] = "ROBLOX", ["minecraft"] = "MINECRAFT", ["fallguys_client_game"] = "FALLGUYS",
    };

    /// <summary>Fragmentos de ruta (en minúsculas) de las carpetas donde los lanzadores instalan juegos.</summary>
    private static readonly string[] LauncherPaths =
    {
        "\\steamapps\\common\\", "\\epic games\\", "\\riot games\\", "\\gog galaxy\\games\\", "\\xboxgames\\",
        "\\ubisoft game launcher\\games\\", "\\origin games\\", "\\ea games\\",
    };

    private readonly record struct Sample(DateTime Time, string? Tag);

    private readonly object _gate = new();
    private readonly Queue<Sample> _samples = new();
    private readonly Dictionary<int, (string Name, string Path)> _pidCache = new();
    private readonly Timer _timer;
    private IReadOnlyDictionary<string, string> _aliases = new Dictionary<string, string>();
    private bool _disposed;

    public GameDetector()
    {
        _timer = new Timer(_ => TakeSample(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    /// <summary>Alias del usuario: nombre de proceso (sin .exe) → sigla.</summary>
    public void SetAliases(IDictionary<string, string>? aliases)
    {
        _aliases = aliases is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(aliases, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Sigla del juego dominante en los últimos <paramref name="window"/>, o null si no había ninguno.</summary>
    public string? DetectRecent(TimeSpan window)
    {
        DateTime since = DateTime.UtcNow - window;
        lock (_gate)
        {
            var best = _samples
                .Where(s => s.Tag is not null && s.Time >= since)
                .GroupBy(s => s.Tag!)
                .Select(g => (Tag: g.Key, Seconds: g.Count()))
                .OrderByDescending(g => g.Seconds)
                .FirstOrDefault();
            return best.Seconds >= MinSecondsToName ? best.Tag : null;
        }
    }

    /// <summary>Sigla de un programa o null si no parece un juego. Pública para las pruebas.</summary>
    public static string? TagFor(string processName, string? path, IReadOnlyDictionary<string, string>? aliases)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        if (aliases is not null)
        {
            foreach (var (key, value) in aliases)
            {
                if (string.Equals(key, processName, StringComparison.OrdinalIgnoreCase) && Sanitize(value) is { Length: > 0 } custom)
                {
                    return custom;
                }
            }
        }
        if (KnownGames.TryGetValue(processName, out var known)) return known;
        if (path is not null)
        {
            string lower = path.ToLowerInvariant();
            if (LauncherPaths.Any(lower.Contains)) return Sanitize(processName) is { Length: > 0 } tag ? tag : null;
        }
        return null;
    }

    /// <summary>Deja solo letras y dígitos, en mayúsculas y hasta 16 caracteres (apto para un nombre de archivo).</summary>
    public static string Sanitize(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c) && c < 128) sb.Append(char.ToUpperInvariant(c));
            if (sb.Length >= 16) break;
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }

    private void TakeSample()
    {
        try
        {
            string? tag = null;
            var (name, path) = ForegroundProcess();
            if (name is not null) tag = TagFor(name, path, _aliases);
            lock (_gate)
            {
                _samples.Enqueue(new Sample(DateTime.UtcNow, tag));
                while (_samples.Count > MaxSamples) _samples.Dequeue();
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Proceso que terminó justo ahora: se ignora esta muestra.
        }
    }

    private (string? Name, string? Path) ForegroundProcess()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return (null, null);
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return (null, null);
        if (_pidCache.TryGetValue((int)pid, out var cached)) return cached;

        IntPtr process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == IntPtr.Zero) return (null, null);
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (!QueryFullProcessImageName(process, 0, sb, ref size)) return (null, null);
            string path = sb.ToString();
            var result = (Path.GetFileNameWithoutExtension(path), path);
            if (_pidCache.Count > 200) _pidCache.Clear();
            _pidCache[(int)pid] = result;
            return result;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
}
