using ReplayClip.Hotkeys;

namespace ReplayClip.Settings;

/// <summary>Qué se captura.</summary>
public enum CaptureMode
{
    /// <summary>Un monitor completo (por defecto: aguanta alt-tab y ventanas que se recrean).</summary>
    Monitor,
    /// <summary>Sigue a la ventana que esté en primer plano.</summary>
    ForegroundWindow,
    /// <summary>Una ventana concreta elegida por el usuario (se busca por proceso y título).</summary>
    Window,
    /// <summary>La ventana principal de un proceso (p. ej. "League of Legends"); se reengancha sola.</summary>
    Process,
}

/// <summary>
/// Ajustes persistentes. Todos los valores se validan con <see cref="Normalize"/> al cargar y al guardar.
/// </summary>
public sealed class AppSettings
{
    public const int MinReplaySeconds = 15;
    public const int MaxReplaySeconds = 120;
    public const int MinBitrateMbps = 8;
    public const int MaxBitrateMbps = 25;
    public const int MinSystemGainPercent = 100;
    public const int MaxSystemGainPercent = 800;

    /// <summary>Segundos del búfer de repetición (15 a 120).</summary>
    public int ReplaySeconds { get; set; } = 30;

    /// <summary>30 o 60.</summary>
    public int FramesPerSecond { get; set; } = 60;

    /// <summary>Bitrate de video en Mbps (8 a 25).</summary>
    public int BitrateMbps { get; set; } = 20;

    /// <summary>Altura máxima de salida: 720 o 1080.</summary>
    public int MaxOutputHeight { get; set; } = 1080;

    public string OutputFolder { get; set; } = DefaultOutputFolder;

    public string SaveReplayHotkey { get; set; } = "Ctrl+Shift+F9";

    public string ToggleRecordingHotkey { get; set; } = "Ctrl+Shift+F10";

    public CaptureMode CaptureMode { get; set; } = CaptureMode.Monitor;

    /// <summary>Nombre de dispositivo del monitor (\\.\DISPLAY1). Vacío = principal.</summary>
    public string MonitorDeviceName { get; set; } = "";

    /// <summary>Nombre del proceso (sin .exe) para los modos Window y Process.</summary>
    public string TargetProcessName { get; set; } = "";

    /// <summary>Título de la ventana para el modo Window.</summary>
    public string TargetWindowTitle { get; set; } = "";

    public bool CaptureSystemAudio { get; set; } = true;

    /// <summary>Ganancia del audio del sistema en % (100 a 800): sube el volumen del juego en los clips.</summary>
    public int SystemGainPercent { get; set; } = 100;

    public bool CaptureMicrophone { get; set; } = false;

    /// <summary>Id del micrófono (MMDevice.ID). Vacío = el predeterminado de comunicaciones/consola.</summary>
    public string MicrophoneDeviceId { get; set; } = "";

    /// <summary>Grabar el cursor (solo configurable desde Windows 10 2004).</summary>
    public bool CaptureCursor { get; set; } = true;

    public bool StartWithWindows { get; set; } = false;

    /// <summary>
    /// Modo monitor con Desktop Duplication (sin borde amarillo en Windows 10). Si se desactiva, o si no está
    /// disponible, se usa Windows.Graphics.Capture.
    /// </summary>
    public bool UseDesktopDuplication { get; set; } = true;

    /// <summary>Sonido corto al guardar un clip y al iniciar o detener la grabación.</summary>
    public bool PlaySounds { get; set; } = true;

    /// <summary>Empezar el nombre de cada clip con la sigla del juego detectado (LOL_Clip_..., TWD_Clip_...).</summary>
    public bool NameClipsByGame { get; set; } = true;

    /// <summary>
    /// Siglas propias: nombre del proceso (sin .exe) a sigla. Sirve para juegos que la app no reconoce,
    /// p. ej. "MiJuego": "MJ". Se edita en settings.json.
    /// </summary>
    public Dictionary<string, string> GameAliases { get; set; } = new();

    public static string DefaultOutputFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ReplayClip");

    /// <summary>Corrige valores fuera de rango y devuelve la misma instancia.</summary>
    public AppSettings Normalize()
    {
        ReplaySeconds = Math.Clamp(ReplaySeconds, MinReplaySeconds, MaxReplaySeconds);
        FramesPerSecond = FramesPerSecond >= 45 ? 60 : 30;
        BitrateMbps = Math.Clamp(BitrateMbps, MinBitrateMbps, MaxBitrateMbps);
        MaxOutputHeight = MaxOutputHeight > 720 ? 1080 : 720;
        SystemGainPercent = Math.Clamp(SystemGainPercent, MinSystemGainPercent, MaxSystemGainPercent);
        if (string.IsNullOrWhiteSpace(OutputFolder)) OutputFolder = DefaultOutputFolder;
        if (!HotkeyGesture.TryParse(SaveReplayHotkey, out _)) SaveReplayHotkey = "Ctrl+Shift+F9";
        if (!HotkeyGesture.TryParse(ToggleRecordingHotkey, out _)) ToggleRecordingHotkey = "Ctrl+Shift+F10";
        if (!Enum.IsDefined(CaptureMode)) CaptureMode = CaptureMode.Monitor;
        MonitorDeviceName ??= "";
        TargetProcessName ??= "";
        TargetWindowTitle ??= "";
        MicrophoneDeviceId ??= "";
        GameAliases ??= new();
        return this;
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>
    /// RAM estimada del búfer en MB: video a bitrate constante durante N+2 s más audio (3 pistas a 160 kbps).
    /// </summary>
    public double EstimatedBufferMegabytes()
    {
        double seconds = ReplaySeconds + 2;
        double videoBytes = BitrateMbps * 1_000_000 / 8.0 * seconds;
        double audioBytes = 3 * 20_000 * seconds;
        return (videoBytes + audioBytes) / (1024 * 1024);
    }

    /// <summary>True si cambiar de <paramref name="other"/> a este objeto exige reiniciar la captura.</summary>
    public bool RequiresPipelineRestart(AppSettings other) =>
        FramesPerSecond != other.FramesPerSecond
        || BitrateMbps != other.BitrateMbps
        || MaxOutputHeight != other.MaxOutputHeight
        || CaptureMode != other.CaptureMode
        || MonitorDeviceName != other.MonitorDeviceName
        || TargetProcessName != other.TargetProcessName
        || TargetWindowTitle != other.TargetWindowTitle
        || CaptureSystemAudio != other.CaptureSystemAudio
        || SystemGainPercent != other.SystemGainPercent
        || CaptureMicrophone != other.CaptureMicrophone
        || MicrophoneDeviceId != other.MicrophoneDeviceId
        || CaptureCursor != other.CaptureCursor
        || UseDesktopDuplication != other.UseDesktopDuplication;
}
