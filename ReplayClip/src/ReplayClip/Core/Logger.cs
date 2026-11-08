using System.Collections.Concurrent;
using System.Text;

namespace ReplayClip.Core;

/// <summary>
/// Registro local en %LOCALAPPDATA%\ReplayClip\logs. Escribe en un hilo de fondo de baja prioridad
/// para no bloquear los hilos de captura. Rota al llegar a 1 MB y conserva 3 archivos. Sin red.
/// </summary>
public static class Logger
{
    private const long MaxFileBytes = 1024 * 1024;
    private const int MaxFiles = 3;

    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 2000);
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReplayClip", "logs");
    private static Thread? _writerThread;

    /// <summary>Ruta del archivo de registro actual.</summary>
    public static string CurrentLogPath => Path.Combine(LogDirectory, "replayclip.log");

    /// <summary>Inicia el hilo de escritura. Llamar una vez al arrancar.</summary>
    public static void Start()
    {
        Directory.CreateDirectory(LogDirectory);
        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "ReplayClip.Logger",
            Priority = ThreadPriority.BelowNormal,
        };
        _writerThread.Start();
    }

    public static void Info(string message) => Enqueue("INFO ", message);

    public static void Warn(string message) => Enqueue("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Enqueue("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8})\n{ex.StackTrace}");

    /// <summary>Vacía la cola antes de salir (máximo 1 s).</summary>
    public static void Flush()
    {
        Queue.CompleteAdding();
        _writerThread?.Join(1000);
    }

    private static void Enqueue(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{Environment.CurrentManagedThreadId,3}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        // Si la cola está llena se descarta la línea: nunca bloqueamos un hilo de captura por el registro.
        try { Queue.TryAdd(line); } catch (InvalidOperationException) { /* ya cerrado */ }
    }

    private static void WriterLoop()
    {
        var sb = new StringBuilder();
        foreach (string line in Queue.GetConsumingEnumerable())
        {
            sb.Clear().AppendLine(line);
            while (Queue.TryTake(out string? more)) sb.AppendLine(more);
            try
            {
                RotateIfNeeded();
                File.AppendAllText(CurrentLogPath, sb.ToString(), Encoding.UTF8);
            }
            catch (IOException) { /* disco lleno o archivo bloqueado: se pierde la línea */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void RotateIfNeeded()
    {
        var info = new FileInfo(CurrentLogPath);
        if (!info.Exists || info.Length < MaxFileBytes) return;
        for (int i = MaxFiles - 1; i >= 1; i--)
        {
            string src = i == 1 ? CurrentLogPath : Path.Combine(LogDirectory, $"replayclip.{i - 1}.log");
            string dst = Path.Combine(LogDirectory, $"replayclip.{i}.log");
            if (File.Exists(src)) File.Move(src, dst, overwrite: true);
        }
    }
}
