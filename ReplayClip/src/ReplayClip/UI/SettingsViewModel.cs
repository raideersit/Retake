using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using NAudio.CoreAudioApi;
using ReplayClip.Capture;
using ReplayClip.Hotkeys;
using ReplayClip.Settings;

namespace ReplayClip.UI;

/// <summary>Opción de lista con texto visible.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Estado editable de la ventana de ajustes. Se convierte en <see cref="AppSettings"/> al guardar.</summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private int _replaySeconds;
    private int _bitrateMbps;
    private string _estimate = "";
    private CaptureMode _captureMode;
    private string _status = "";

    private int _systemGainPercent;
    private int _microphoneGainPercent;

    public SettingsViewModel(AppSettings settings)
    {
        _replaySeconds = settings.ReplaySeconds;
        _bitrateMbps = settings.BitrateMbps;
        FramesPerSecond = settings.FramesPerSecond;
        MaxOutputHeight = settings.MaxOutputHeight;
        OutputFolder = settings.OutputFolder;
        SaveHotkey = settings.SaveReplayHotkey;
        RecordHotkey = settings.ToggleRecordingHotkey;
        _captureMode = settings.CaptureMode;
        CaptureSystemAudio = settings.CaptureSystemAudio;
        _systemGainPercent = settings.SystemGainPercent;
        _microphoneGainPercent = settings.MicrophoneGainPercent;
        CaptureMicrophone = settings.CaptureMicrophone;
        CaptureCursor = settings.CaptureCursor;
        StartWithWindows = settings.StartWithWindows;
        UseDesktopDuplication = settings.UseDesktopDuplication;
        PlaySounds = settings.PlaySounds;
        NameClipsByGame = settings.NameClipsByGame;
        _gameAliases = settings.GameAliases;

        RefreshSources();
        SelectedMonitor = Monitors.FirstOrDefault(m => m.Value == settings.MonitorDeviceName) ?? Monitors.FirstOrDefault();
        SelectedWindow = Windows.FirstOrDefault(w => w.Value.ProcessName == settings.TargetProcessName && w.Value.Title == settings.TargetWindowTitle);
        ProcessName = settings.TargetProcessName;
        WindowTitle = settings.TargetWindowTitle;
        SelectedMicrophone = Microphones.FirstOrDefault(m => m.Value == settings.MicrophoneDeviceId) ?? Microphones.FirstOrDefault();
        UpdateEstimate();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<Choice<int>> FpsChoices { get; } = new[] { new Choice<int>(30, "30 fps"), new Choice<int>(60, "60 fps") };

    public IReadOnlyList<Choice<int>> HeightChoices { get; } = new[] { new Choice<int>(720, "720p"), new Choice<int>(1080, "1080p") };

    public IReadOnlyList<Choice<CaptureMode>> ModeChoices { get; } = new[]
    {
        new Choice<CaptureMode>(CaptureMode.Monitor, "Monitor completo"),
        new Choice<CaptureMode>(CaptureMode.ForegroundWindow, "Ventana en primer plano"),
        new Choice<CaptureMode>(CaptureMode.Window, "Ventana elegida"),
        new Choice<CaptureMode>(CaptureMode.Process, "Juego / proceso (se reengancha solo)"),
    };

    public ObservableCollection<Choice<string>> Monitors { get; } = new();

    public ObservableCollection<Choice<WindowInfo>> Windows { get; } = new();

    public ObservableCollection<string> Processes { get; } = new();

    public ObservableCollection<Choice<string>> Microphones { get; } = new();

    public int ReplaySeconds
    {
        get => _replaySeconds;
        set { _replaySeconds = Math.Clamp(value, AppSettings.MinReplaySeconds, AppSettings.MaxReplaySeconds); OnChanged(); UpdateEstimate(); }
    }

    public int BitrateMbps
    {
        get => _bitrateMbps;
        set { _bitrateMbps = Math.Clamp(value, AppSettings.MinBitrateMbps, AppSettings.MaxBitrateMbps); OnChanged(); UpdateEstimate(); }
    }

    public int FramesPerSecond { get; set; }

    public int MaxOutputHeight { get; set; }

    public string OutputFolder { get; set; }

    public string SaveHotkey { get; set; }

    public string RecordHotkey { get; set; }

    public CaptureMode CaptureMode
    {
        get => _captureMode;
        set
        {
            _captureMode = value;
            OnChanged();
            OnChanged(nameof(IsMonitorMode));
            OnChanged(nameof(IsWindowMode));
            OnChanged(nameof(IsProcessMode));
        }
    }

    public bool IsMonitorMode => CaptureMode == CaptureMode.Monitor;

    public bool IsWindowMode => CaptureMode == CaptureMode.Window;

    public bool IsProcessMode => CaptureMode == CaptureMode.Process;

    public Choice<string>? SelectedMonitor { get; set; }

    public Choice<WindowInfo>? SelectedWindow { get; set; }

    public string ProcessName { get; set; } = "";

    public string WindowTitle { get; set; } = "";

    public bool CaptureSystemAudio { get; set; }

    public int MicrophoneGainPercent
    {
        get => _microphoneGainPercent;
        set { _microphoneGainPercent = Math.Clamp(value, AppSettings.MinMicrophoneGainPercent, AppSettings.MaxMicrophoneGainPercent); OnChanged(); }
    }

    public int SystemGainPercent
    {
        get => _systemGainPercent;
        set { _systemGainPercent = Math.Clamp(value, AppSettings.MinSystemGainPercent, AppSettings.MaxSystemGainPercent); OnChanged(); }
    }

    public bool CaptureMicrophone { get; set; }

    public Choice<string>? SelectedMicrophone { get; set; }

    public bool CaptureCursor { get; set; }

    public bool StartWithWindows { get; set; }

    public bool UseDesktopDuplication { get; set; }

    public bool PlaySounds { get; set; }

    public bool NameClipsByGame { get; set; }

    // Los alias no se editan en la ventana: se conservan tal cual al guardar.
    private readonly Dictionary<string, string> _gameAliases;

    /// <summary>"RAM estimada del búfer: 80 MB".</summary>
    public string MemoryEstimate
    {
        get => _estimate;
        private set { _estimate = value; OnChanged(); }
    }

    /// <summary>Estado en vivo del motor (codificador, memoria...).</summary>
    public string Status
    {
        get => _status;
        set { _status = value; OnChanged(); }
    }

    /// <summary>Vuelve a enumerar monitores, ventanas y micrófonos.</summary>
    public void RefreshSources()
    {
        Monitors.Clear();
        foreach (var monitor in WindowEnumerator.GetMonitors())
        {
            Monitors.Add(new Choice<string>(monitor.IsPrimary ? "" : monitor.DeviceName, monitor.ToString()));
        }

        Windows.Clear();
        Processes.Clear();
        var windows = WindowEnumerator.GetCapturableWindows();
        foreach (var window in windows.OrderBy(w => w.ProcessName))
        {
            Windows.Add(new Choice<WindowInfo>(window, window.ToString()));
        }
        foreach (string name in windows.Select(w => w.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Order())
        {
            Processes.Add(name);
        }

        Microphones.Clear();
        Microphones.Add(new Choice<string>("", "Predeterminado del sistema"));
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device) Microphones.Add(new Choice<string>(device.ID, device.FriendlyName));
            }
        }
        catch (System.Runtime.InteropServices.COMException) { /* sin servicio de audio */ }
    }

    /// <summary>Valida y devuelve el texto del error, o null si todo es correcto.</summary>
    public string? Validate()
    {
        if (!HotkeyGesture.TryParse(SaveHotkey, out var save)) return "El atajo para guardar no es válido.";
        if (!HotkeyGesture.TryParse(RecordHotkey, out var record)) return "El atajo para grabar no es válido.";
        if (save == record) return "Los dos atajos no pueden ser iguales.";
        if (string.IsNullOrWhiteSpace(OutputFolder)) return "Elige una carpeta de salida.";
        try { _ = Path.GetFullPath(OutputFolder); }
        catch (Exception) { return "La carpeta de salida no es válida."; }
        if (CaptureMode == CaptureMode.Window && SelectedWindow is null && string.IsNullOrEmpty(ProcessName))
            return "Elige una ventana.";
        if (CaptureMode == CaptureMode.Process && string.IsNullOrWhiteSpace(ProcessName))
            return "Escribe o elige el nombre del proceso.";
        return null; // sin audio también es válido: solo video
    }

    public AppSettings ToSettings()
    {
        string processName = ProcessName.Trim();
        if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) processName = processName[..^4];
        string title = WindowTitle;
        if (CaptureMode == CaptureMode.Window && SelectedWindow is not null)
        {
            processName = SelectedWindow.Value.ProcessName;
            title = SelectedWindow.Value.Title;
        }
        return new AppSettings
        {
            ReplaySeconds = ReplaySeconds,
            FramesPerSecond = FramesPerSecond,
            BitrateMbps = BitrateMbps,
            MaxOutputHeight = MaxOutputHeight,
            OutputFolder = OutputFolder.Trim(),
            SaveReplayHotkey = SaveHotkey,
            ToggleRecordingHotkey = RecordHotkey,
            CaptureMode = CaptureMode,
            MonitorDeviceName = SelectedMonitor?.Value ?? "",
            TargetProcessName = processName,
            TargetWindowTitle = CaptureMode == CaptureMode.Window ? title : "",
            CaptureSystemAudio = CaptureSystemAudio,
            SystemGainPercent = SystemGainPercent,
            MicrophoneGainPercent = MicrophoneGainPercent,
            CaptureMicrophone = CaptureMicrophone,
            MicrophoneDeviceId = SelectedMicrophone?.Value ?? "",
            CaptureCursor = CaptureCursor,
            StartWithWindows = StartWithWindows,
            UseDesktopDuplication = UseDesktopDuplication,
            PlaySounds = PlaySounds,
            NameClipsByGame = NameClipsByGame,
            GameAliases = _gameAliases,
        }.Normalize();
    }

    private void UpdateEstimate()
    {
        var probe = new AppSettings { ReplaySeconds = ReplaySeconds, BitrateMbps = BitrateMbps };
        double mb = probe.EstimatedBufferMegabytes();
        string warning = mb + 70 > 200 ? "  (supera el objetivo de 200 MB en total)" : "";
        MemoryEstimate = $"RAM estimada del búfer: {mb:F0} MB{warning}";
    }

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
