using System.Diagnostics;
using ReplayClip.Core;
using ReplayClip.Settings;
using ReplayClip.SmokeTest;
using Vortice.MediaFoundation;

// Uso: ReplayClip.SmokeTest --out <carpeta> [--replay 30] [--record 8] [--soak-minutes 0]
//      [--mode monitor|process|window] [--process "nombre"] [--mic] [--fps 60] [--bitrate 20]
var options = ParseArgs(args);
string outDir = options.GetValueOrDefault("out") ?? Path.Combine(Path.GetTempPath(), "ReplayClipSmoke");
int replay = int.Parse(options.GetValueOrDefault("replay") ?? "30");
int record = int.Parse(options.GetValueOrDefault("record") ?? "8");
double soakMinutes = double.Parse(options.GetValueOrDefault("soak-minutes") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
string? inspectOnly = options.GetValueOrDefault("inspect");

Logger.Start();
MediaFactory.MFStartup(true).CheckError();

if (inspectOnly is not null)
{
    Report(inspectOnly, null);
    if (options.GetValueOrDefault("png") is string png) FrameExtractor.SavePng(inspectOnly, 5, png);
    return 0;
}

var settings = new AppSettings
{
    ReplaySeconds = replay,
    FramesPerSecond = int.Parse(options.GetValueOrDefault("fps") ?? "60"),
    BitrateMbps = int.Parse(options.GetValueOrDefault("bitrate") ?? "20"),
    OutputFolder = outDir,
    CaptureMode = Enum.Parse<CaptureMode>(options.GetValueOrDefault("mode") ?? "Monitor", ignoreCase: true),
    TargetProcessName = options.GetValueOrDefault("process") ?? "",
    CaptureSystemAudio = true,
    CaptureMicrophone = options.ContainsKey("mic"),
    UseDesktopDuplication = !options.ContainsKey("wgc"),
    CaptureCursor = !options.ContainsKey("nocursor"),
}.Normalize();

Console.WriteLine($"Salida: {outDir}  Repetición: {replay} s  Modo: {settings.CaptureMode}  Mic: {settings.CaptureMicrophone}");
var files = new List<(string Path, double? Expected)>();
var engine = new RecorderEngine(settings);
TaskCompletionSource<string>? waiting = null;
engine.Notification += n =>
{
    Console.WriteLine($"  [aviso {n.Kind}] {n.Title}: {n.Message}");
    if (n.FilePath is not null) waiting?.TrySetResult(n.FilePath);
};

using var process = Process.GetCurrentProcess();
engine.Start();
Console.WriteLine($"  {engine.VideoDescription}");

// 1) Llenar el búfer y medir consumo en régimen estable.
Measure(engine, process, TimeSpan.FromSeconds(replay + 4), "Llenando búfer");

// 2) Guardar la repetición.
waiting = new TaskCompletionSource<string>();
await engine.SaveReplayAsync();
string clip = await waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
files.Add((clip, replay));

// 3) Grabación continua.
if (record > 0)
{
    engine.ToggleRecording();
    Measure(engine, process, TimeSpan.FromSeconds(record), "Grabando");
    waiting = new TaskCompletionSource<string>();
    engine.ToggleRecording();
    files.Add((await waiting.Task.WaitAsync(TimeSpan.FromSeconds(30)), record));
}

// 4) Prueba de resistencia: la memoria no debe crecer.
if (soakMinutes > 0)
{
    var end = DateTime.UtcNow.AddMinutes(soakMinutes);
    int round = 0;
    while (DateTime.UtcNow < end)
    {
        Measure(engine, process, TimeSpan.FromMinutes(Math.Min(5, (end - DateTime.UtcNow).TotalMinutes)), $"Resistencia {++round}");
        waiting = new TaskCompletionSource<string>();
        await engine.SaveReplayAsync();
        files.Add((await waiting.Task.WaitAsync(TimeSpan.FromSeconds(30)), replay));
    }
}

var sw = Stopwatch.StartNew();
engine.Dispose();
Console.WriteLine($"Cierre limpio en {sw.ElapsedMilliseconds} ms.");

int failures = 0;
foreach (var (path, expected) in files) failures += Report(path, expected) ? 0 : 1;
MediaFactory.MFShutdown();
Logger.Flush();
Console.WriteLine(failures == 0 ? "RESULTADO: OK" : $"RESULTADO: {failures} archivo(s) con problemas");
return failures == 0 ? 0 : 1;

static void Measure(RecorderEngine engine, Process process, TimeSpan duration, string label)
{
    process.Refresh();
    var cpuStart = process.TotalProcessorTime;
    var wall = Stopwatch.StartNew();
    while (wall.Elapsed < duration)
    {
        Thread.Sleep(TimeSpan.FromSeconds(Math.Min(5, Math.Max(0.1, (duration - wall.Elapsed).TotalSeconds))));
        process.Refresh();
        double cpu = (process.TotalProcessorTime - cpuStart).TotalMilliseconds / wall.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
        Console.WriteLine($"  {label}: t={wall.Elapsed.TotalSeconds,5:F0}s CPU={cpu,5:F2}%  privada={process.PrivateMemorySize64 / 1048576,4} MB  " +
                          $"working set={process.WorkingSet64 / 1048576,4} MB  búfer={engine.BufferedSeconds,5:F1}s/{engine.BufferBytes / 1048576} MB  " +
                          $"GC={GC.GetTotalMemory(false) / 1048576} MB");
        Console.WriteLine($"      {engine.VideoStatistics}");
    }
}

static bool Report(string path, double? expected)
{
    var (duration, tracks) = Mp4Inspector.Inspect(path);
    Console.WriteLine($"\n{Path.GetFileName(path)}  ({new FileInfo(path).Length / 1048576.0:F1} MB)  duración {duration:F2} s");
    bool ok = true;
    foreach (var t in tracks)
    {
        Console.WriteLine($"  pista {t.Index}: {t.Type,-5} muestras={t.Samples,5} claves={t.KeyFrames,4} " +
                          $"inicio={t.FirstSeconds:F3}s fin={t.LastSeconds:F3}s máx. entre claves={t.MaxKeyFrameGapSeconds:F2}s");
        if (t.Samples == 0) ok = false;
    }
    var video = tracks.FirstOrDefault(t => t.Type == "H.264");
    if (video is null) { Console.WriteLine("  ERROR: no hay video"); return false; }
    if (video.Type == "H.264" && video.MaxKeyFrameGapSeconds > 1.25) { Console.WriteLine("  ERROR: fotogramas clave a más de 1 s"); ok = false; }
    foreach (var audio in tracks.Where(t => t.Type == "AAC"))
    {
        double offset = audio.FirstSeconds - video.FirstSeconds;
        double endDiff = audio.LastSeconds - video.LastSeconds;
        Console.WriteLine($"  sincronía pista {audio.Index}: inicio {offset * 1000:F0} ms, fin {endDiff * 1000:F0} ms respecto al video");
        if (Math.Abs(offset) > 0.1 || Math.Abs(endDiff) > 0.15) { Console.WriteLine("  ERROR: audio desincronizado"); ok = false; }
    }
    if (expected is double n && Math.Abs(duration - n) > 1.0 && n > 0)
    {
        Console.WriteLine($"  ERROR: se esperaban ~{n} s (±1 s)");
        ok = false;
    }
    Console.WriteLine(ok ? "  OK" : "  FALLÓ");
    return ok;
}

static Dictionary<string, string?> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) continue;
        string key = args[i][2..];
        string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
        result[key] = value;
    }
    return result;
}
