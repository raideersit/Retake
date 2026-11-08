using ReplayClip.Audio;
using ReplayClip.Buffer;
using ReplayClip.Capture;
using ReplayClip.Muxing;
using ReplayClip.Settings;

namespace ReplayClip.Core;

/// <summary>Tipo de aviso para la interfaz.</summary>
public enum NotificationKind { Info, Success, Warning, Error }

/// <summary>Sonido de confirmación asociado a un aviso.</summary>
public enum NotificationSound { None, ClipSaved, RecordingStarted, RecordingStopped, Error }

/// <summary>Aviso que la interfaz muestra como notificación de la bandeja (y, si procede, con un sonido).</summary>
public sealed record EngineNotification(NotificationKind Kind, string Title, string Message, string? FilePath = null,
    NotificationSound Sound = NotificationSound.None);

/// <summary>
/// Orquestador: crea y vigila las cadenas de video y audio, mantiene el búfer de repetición y atiende
/// las órdenes de la interfaz (guardar repetición, grabar). Recupera solo los fallos: dispositivo
/// perdido, ventana cerrada, cambio de primer plano, disco lleno.
/// </summary>
public sealed class RecorderEngine : IDisposable
{
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromMilliseconds(500);

    private readonly EncodedSampleBus _bus = new();
    private readonly object _lifecycleLock = new();
    private readonly Timer _monitorTimer;
    private AppSettings _settings;
    private ReplayRingBuffer? _ring;
    private VideoPipeline? _video;
    private AudioPipeline? _audio;
    private CaptureTargetResolver? _resolver;
    private RecordingWriter? _recording;
    private Task _pendingSave = Task.CompletedTask;
    private int _generation;
    private volatile bool _targetClosed;
    private DateTime _nextVideoRetry = DateTime.MinValue;
    private int _consecutiveVideoFailures;
    private bool _disposed;

    public RecorderEngine(AppSettings settings)
    {
        _settings = settings.Clone();
        _monitorTimer = new Timer(_ => MonitorTick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Avisos para el usuario. Se invoca desde hilos de fondo.</summary>
    public event Action<EngineNotification>? Notification;

    /// <summary>Cambió el estado (grabando, codificador...). Se invoca desde hilos de fondo.</summary>
    public event Action? StatusChanged;

    public bool IsRecording => _recording is not null;

    public bool IsSaving => !_pendingSave.IsCompleted;

    /// <summary>Descripción de la cadena de video activa para la ventana de ajustes.</summary>
    public string VideoDescription => _video?.Description ?? "Captura detenida";

    /// <summary>Contadores de diagnóstico de la cadena de video.</summary>
    public string VideoStatistics => _video?.Statistics ?? "";

    public string CaptureDescription => _video?.CurrentTarget?.Description ?? "Sin objetivo (imagen en negro)";

    /// <summary>Memoria reservada por el búfer de repetición.</summary>
    public long BufferBytes => _ring?.Pool.ReservedBytes ?? 0;

    public double BufferedSeconds => _ring?.BufferedVideoSeconds ?? 0;

    /// <summary>Arranca la captura con los ajustes actuales.</summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            RecordingWriter.RecoverIncompleteFiles(_settings.OutputFolder);
            _ring = new ReplayRingBuffer(_settings.ReplaySeconds, CapacityFor(_settings));
            _bus.Subscribe(_ring);
            StartPipelinesLocked();
            _monitorTimer.Change(MonitorInterval, MonitorInterval);
        }
    }

    /// <summary>Aplica ajustes nuevos; reinicia la captura solo si algún parámetro de video/audio cambió.</summary>
    public void ApplySettings(AppSettings settings)
    {
        lock (_lifecycleLock)
        {
            var previous = _settings;
            _settings = settings.Clone();
            if (_settings.OutputFolder != previous.OutputFolder) RecordingWriter.RecoverIncompleteFiles(_settings.OutputFolder);
            _ring?.Configure(_settings.ReplaySeconds, CapacityFor(_settings));
            if (_settings.RequiresPipelineRestart(previous))
            {
                if (_recording is not null)
                {
                    StopRecordingLocked("La grabación se cerró porque cambiaron los ajustes de captura.");
                }
                StopPipelinesLocked();
                StartPipelinesLocked();
            }
        }
        StatusChanged?.Invoke();
    }

    /// <summary>Guarda los últimos N segundos en un MP4.</summary>
    public async Task SaveReplayAsync()
    {
        ReplaySnapshot? snapshot;
        string folder;
        lock (_lifecycleLock)
        {
            if (_ring is null) return;
            if (IsSaving)
            {
                Notify(NotificationKind.Info, "Guardando…", "Ya se está guardando un clip.");
                return;
            }
            snapshot = _ring.CreateSnapshot();
            folder = _settings.OutputFolder;
            if (snapshot is null)
            {
                Notify(NotificationKind.Warning, "Aún no hay video", "El búfer todavía está vacío; prueba en unos segundos.",
                    sound: NotificationSound.Error);
                return;
            }
            _pendingSave = ExportAndNotifyAsync(snapshot, folder);
        }
        await _pendingSave.ConfigureAwait(false);
    }

    /// <summary>Inicia o detiene la grabación continua.</summary>
    public void ToggleRecording()
    {
        lock (_lifecycleLock)
        {
            if (_recording is not null)
            {
                StopRecordingLocked(null);
                return;
            }
            try
            {
                var writer = new RecordingWriter(_settings.OutputFolder, _bus.GetLatestFormat);
                writer.Faulted += ex => ThreadPool.QueueUserWorkItem(_ =>
                {
                    lock (_lifecycleLock)
                    {
                        if (ReferenceEquals(_recording, writer)) StopRecordingLocked($"Grabación detenida: {ex.Message}");
                    }
                });
                _recording = writer;
                _bus.Subscribe(writer);
                _video?.RequestKeyFrame(); // empezar sin esperar al siguiente IDR
                Notify(NotificationKind.Info, "Grabando", "Grabación continua iniciada. Pulsa el atajo otra vez para detenerla.",
                    sound: NotificationSound.RecordingStarted);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Notify(NotificationKind.Error, "No se pudo grabar", ex.Message, sound: NotificationSound.Error);
            }
        }
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            _monitorTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        _monitorTimer.Dispose();

        // Orden de cierre limpio: terminar archivos primero, luego liberar la captura.
        lock (_lifecycleLock)
        {
            if (_recording is not null) StopRecordingLocked(null, notify: false);
        }
        try { _pendingSave.Wait(TimeSpan.FromSeconds(15)); }
        catch (AggregateException) { /* ya notificado */ }
        lock (_lifecycleLock)
        {
            StopPipelinesLocked();
            _ring?.Dispose();
            _ring = null;
        }
    }

    /// <summary>Tope de memoria del búfer: bitrate × (N+2) × 1,5 de margen para picos, más el audio.</summary>
    public static long CapacityFor(AppSettings settings) =>
        (long)(settings.EstimatedBufferMegabytes() * 1.5 * 1024 * 1024);

    private void StartPipelinesLocked()
    {
        _resolver = new CaptureTargetResolver(_settings);
        _targetClosed = false;
        try
        {
            _audio = new AudioPipeline(_settings.CaptureSystemAudio, _settings.CaptureMicrophone,
                _settings.MicrophoneDeviceId, _bus, NextGeneration);
            if (_audio.MicrophoneMissing)
            {
                Notify(NotificationKind.Warning, "Micrófono no encontrado", "Se graba sin micrófono. Conéctalo y vuelve a guardar los ajustes.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("No se pudo iniciar el audio", ex);
            Notify(NotificationKind.Warning, "Audio no disponible", "Se graba solo video. Revisa el registro para más detalles.");
            _audio = null;
        }
        TryStartVideoLocked();
    }

    private void TryStartVideoLocked()
    {
        var target = _resolver!.Resolve();
        var reference = _resolver.GetReferenceSize(target);
        var outputSize = CaptureTargetResolver.ComputeOutputSize(reference.Width, reference.Height, _settings.MaxOutputHeight);
        try
        {
            _bus.ClearFormat(StreamKind.Video);
            _video = new VideoPipeline(_settings, target, outputSize, _bus, NextGeneration());
            _video.TargetClosed += _ => _targetClosed = true;
            _consecutiveVideoFailures = 0;
            Logger.Info($"Video: {_video.Description}");
            if (_video.IsSoftwareFallback)
            {
                Notify(NotificationKind.Warning, "Sin codificador de hardware",
                    "Se usa codificación por software a 720p 30 fps; el consumo de CPU será mayor.");
            }
        }
        catch (Exception ex)
        {
            _video = null;
            _consecutiveVideoFailures++;
            var delay = TimeSpan.FromSeconds(Math.Min(30, 2 * _consecutiveVideoFailures));
            _nextVideoRetry = DateTime.UtcNow + delay;
            Logger.Error($"No se pudo iniciar el video (reintento en {delay.TotalSeconds:F0} s)", ex);
            if (_consecutiveVideoFailures == 1)
            {
                Notify(NotificationKind.Error, "No se pudo iniciar la captura", ex.Message);
            }
        }
        StatusChanged?.Invoke();
    }

    private void StopPipelinesLocked()
    {
        _video?.Dispose();
        _video = null;
        _audio?.Dispose();
        _audio = null;
    }

    private void MonitorTick()
    {
        if (!Monitor.TryEnter(_lifecycleLock)) return; // otro cambio en curso: se revisa en el siguiente tick
        try
        {
            if (_disposed || _resolver is null) return;

            if (_video is null)
            {
                if (DateTime.UtcNow >= _nextVideoRetry) TryStartVideoLocked();
                return;
            }

            if (_video.IsBroken)
            {
                // Dispositivo perdido (driver actualizado, GPU reiniciada) o codificador caído.
                Logger.Warn("La cadena de video dejó de funcionar; se recrea.");
                _video.Dispose();
                _video = null;
                _nextVideoRetry = DateTime.UtcNow.AddSeconds(1);
                return;
            }

            if (_targetClosed)
            {
                _targetClosed = false;
                _video.ShowBlack();
                StatusChanged?.Invoke();
            }

            var change = _resolver.CheckForChange(_video.CurrentTarget);
            if (change is not null)
            {
                Logger.Info($"Cambio de objetivo: {change.Description}");
                _video.SwitchTarget(change);
                StatusChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error en la vigilancia del motor", ex);
        }
        finally
        {
            Monitor.Exit(_lifecycleLock);
        }
    }

    private void StopRecordingLocked(string? reason, bool notify = true)
    {
        var writer = _recording;
        if (writer is null) return;
        _recording = null;
        _bus.Unsubscribe(writer);
        // El cierre del archivo puede tardar (vaciar la cola): fuera del hilo de UI.
        var task = Task.Run(() =>
        {
            var files = writer.Stop();
            writer.Dispose();
            if (!notify) return;
            string? path = files.Count > 0 ? files[^1] : null;
            if (reason is not null) Notify(NotificationKind.Warning, "Grabación detenida", reason, path, NotificationSound.Error);
            else if (path is not null) Notify(NotificationKind.Success, "Grabación guardada", path, path, NotificationSound.RecordingStopped);
        });
        if (!notify) task.Wait(TimeSpan.FromSeconds(15));
        StatusChanged?.Invoke();
    }

    private async Task ExportAndNotifyAsync(ReplaySnapshot snapshot, string folder)
    {
        try
        {
            string path = await ClipExporter.ExportAsync(snapshot, folder).ConfigureAwait(false);
            Notify(NotificationKind.Success, "Clip guardado", path, path, NotificationSound.ClipSaved);
        }
        catch (Exception ex)
        {
            Logger.Error("No se pudo guardar el clip", ex);
            Notify(NotificationKind.Error, "No se pudo guardar el clip", ex.Message, sound: NotificationSound.Error);
        }
    }

    private int NextGeneration() => Interlocked.Increment(ref _generation);

    private void Notify(NotificationKind kind, string title, string message, string? path = null,
        NotificationSound sound = NotificationSound.None)
    {
        try { Notification?.Invoke(new EngineNotification(kind, title, message, path, sound)); }
        catch (Exception ex) { Logger.Error("Error mostrando una notificación", ex); }
    }
}
