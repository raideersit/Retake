using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using ReplayClip.Capture;
using ReplayClip.Core;
using ReplayClip.Hotkeys;
using ReplayClip.Settings;
using ReplayClip.UI;
using Vortice.MediaFoundation;

namespace ReplayClip;

/// <summary>
/// Punto de entrada: instancia única, arranque de Media Foundation, motor, atajos y bandeja.
/// No hay ventana principal: la aplicación vive en la bandeja del sistema.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private SettingsStore _store = null!;
    private AppSettings _settings = null!;
    private RecorderEngine? _engine;
    private HotkeyManager? _hotkeys;
    private TrayIconController? _tray;
    private SoundCues? _sounds;
    private SettingsWindow? _settingsWindow;
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mutex = new Mutex(true, @"Local\ReplayClip.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            // No es nuestro: se cierra sin liberarlo (ReleaseMutex lanzaría una excepción).
            mutex.Dispose();
            _shuttingDown = true;
            MessageBox.Show("ReplayClip ya se está ejecutando (mira la bandeja del sistema).", "ReplayClip",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        _singleInstance = mutex;

        Logger.Start();
        Logger.Info($"ReplayClip {typeof(App).Assembly.GetName().Version} en {Environment.OSVersion} ({Environment.ProcessorCount} CPU).");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Logger.Error("Excepción no controlada", args.ExceptionObject as Exception);
            EmergencyShutdown();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("Tarea con excepción no observada", args.Exception);
            args.SetObserved();
        };

        if (!GraphicsCaptureSource.IsSupported())
        {
            MessageBox.Show("Este Windows no admite Windows.Graphics.Capture. Se necesita Windows 10 versión 1903 o superior.",
                "ReplayClip", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // MFSTARTUP_LITE: sin la parte de red de Media Foundation (la aplicación no usa red).
        MediaFactory.MFStartup(useLightVersion: true).CheckError();

        _store = new SettingsStore();
        _settings = _store.Load();
        if (StartupRegistration.IsEnabled() != _settings.StartWithWindows) StartupRegistration.Apply(_settings.StartWithWindows);

        _tray = new TrayIconController();
        _tray.SaveReplayRequested += SaveReplay;
        _tray.ToggleRecordingRequested += ToggleRecording;
        _tray.OpenFolderRequested += OpenOutputFolder;
        _tray.SettingsRequested += ShowSettings;
        _tray.ExitRequested += () => Shutdown();

        _engine = new RecorderEngine(_settings);
        _sounds = new SoundCues();
        _engine.Notification += n => Dispatcher.BeginInvoke(() =>
        {
            if (_settings.PlaySounds && !_shuttingDown) _sounds?.Play(n.Sound);
            _tray?.Notifier.Show(n);
        });
        _engine.StatusChanged += () => Dispatcher.BeginInvoke(UpdateTray);
        _engine.Start();

        _hotkeys = new HotkeyManager();
        RegisterHotkeys();
        UpdateTray();

        SessionEnding += (_, _) => Shutdown();
        bool fromStartup = e.Args.Contains(StartupRegistration.StartupArgument);
        if (!fromStartup)
        {
            _tray.Notifier.Show(new EngineNotification(NotificationKind.Info, "ReplayClip está activo",
                $"{_settings.SaveReplayHotkey} guarda los últimos {_settings.ReplaySeconds} s. " +
                $"{_settings.ToggleRecordingHotkey} inicia o detiene la grabación."));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ShutdownServices();
        base.OnExit(e);
    }

    private void ShutdownServices()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        Logger.Info("Cerrando ReplayClip…");
        _settingsWindow?.Close();
        _hotkeys?.Dispose();
        _engine?.Dispose(); // termina grabación y clips en curso antes de liberar la captura
        _tray?.Dispose();
        _tray = null; // las notificaciones que lleguen tarde se ignoran
        _sounds?.Dispose();
        _sounds = null;
        try { MediaFactory.MFShutdown(); } catch (Exception) { }
        Logger.Info("ReplayClip cerrado.");
        Logger.Flush();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
    }

    /// <summary>Último recurso ante un fallo grave: intentar cerrar los archivos abiertos.</summary>
    private void EmergencyShutdown()
    {
        try { _engine?.Dispose(); } catch (Exception) { }
        Logger.Flush();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("Excepción en el hilo de interfaz", e.Exception);
        e.Handled = true; // la captura sigue; se informa al usuario
        if (_shuttingDown) return;
        _tray?.Notifier.Show(new EngineNotification(NotificationKind.Error, "Error inesperado", e.Exception.Message));
    }

    private void RegisterHotkeys()
    {
        if (_hotkeys is null) return;
        _hotkeys.UnregisterAll();
        var failed = new List<string>();
        if (!HotkeyGesture.TryParse(_settings.SaveReplayHotkey, out var save) || !_hotkeys.TryRegister(save, SaveReplay, out _))
            failed.Add(_settings.SaveReplayHotkey);
        if (!HotkeyGesture.TryParse(_settings.ToggleRecordingHotkey, out var record) || !_hotkeys.TryRegister(record, ToggleRecording, out _))
            failed.Add(_settings.ToggleRecordingHotkey);
        if (failed.Count > 0)
        {
            _tray?.Notifier.Show(new EngineNotification(NotificationKind.Warning, "Atajo ocupado",
                $"Otra aplicación usa {string.Join(" y ", failed)}. Cámbialo en Ajustes."));
        }
    }

    private void SaveReplay() => _ = _engine?.SaveReplayAsync();

    private void ToggleRecording() => _engine?.ToggleRecording();

    private void OpenOutputFolder()
    {
        Directory.CreateDirectory(_settings.OutputFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.OutputFolder}\"") { UseShellExecute = true });
    }

    private void UpdateTray()
    {
        _tray?.UpdateState(_engine?.IsRecording ?? false, _settings.SaveReplayHotkey, _settings.ToggleRecordingHotkey, _settings.ReplaySeconds);
    }

    private void ShowSettings()
    {
        if (_engine is null) return;
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        // Mientras se editan los atajos no deben dispararse (y así la caja puede leer la combinación).
        _hotkeys?.UnregisterAll();
        _settingsWindow = new SettingsWindow(_settings, _engine);
        _settingsWindow.Closed += (_, _) =>
        {
            var result = _settingsWindow.Result;
            _settingsWindow = null;
            if (result is not null) ApplySettings(result);
            RegisterHotkeys();
            UpdateTray();
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ApplySettings(AppSettings settings)
    {
        try
        {
            _store.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error("No se pudieron guardar los ajustes", ex);
        }
        _settings = settings;
        StartupRegistration.Apply(settings.StartWithWindows);
        var engine = _engine;
        // Reiniciar la captura tarda ~1 s: fuera del hilo de interfaz.
        _ = Task.Run(() => engine?.ApplySettings(settings));
    }
}
