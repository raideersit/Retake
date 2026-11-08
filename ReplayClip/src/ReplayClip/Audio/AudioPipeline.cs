using ReplayClip.Core;

namespace ReplayClip.Audio;

/// <summary>
/// Cadena de audio completa en un único hilo que despierta cada 10 ms: sondea las fuentes WASAPI,
/// mezcla y codifica a AAC. Pistas resultantes (la 1 es la que oyen todos los reproductores):
/// <list type="bullet">
/// <item>Sistema y micrófono: 1 = mezcla, 2 = sistema, 3 = micrófono.</item>
/// <item>Solo una fuente: 1 = esa fuente.</item>
/// </list>
/// </summary>
public sealed class AudioPipeline : IDisposable
{
    private readonly EncodedSampleBus _bus;
    private readonly Func<int> _nextGeneration;
    private readonly WasapiCaptureSource? _system;
    private readonly WasapiCaptureSource? _microphone;
    private readonly AudioMixer _mixer = new();
    private readonly AudioDeviceWatcher _watcher = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private AacEncoder? _mixEncoder, _systemEncoder, _micEncoder;

    /// <param name="captureSystem">Grabar el audio del sistema.</param>
    /// <param name="captureMicrophone">Grabar el micrófono (se omite si no hay ninguno conectado).</param>
    /// <param name="microphoneId">Micrófono concreto o vacío para el predeterminado.</param>
    public AudioPipeline(bool captureSystem, bool captureMicrophone, string? microphoneId, EncodedSampleBus bus, Func<int> nextGeneration)
    {
        _bus = bus;
        _nextGeneration = nextGeneration;
        if (captureSystem) _system = new WasapiCaptureSource(loopback: true, null, "Audio del sistema");
        if (captureMicrophone)
        {
            if (WasapiCaptureSource.DeviceExists(loopback: false, microphoneId))
            {
                _microphone = new WasapiCaptureSource(loopback: false, microphoneId, "Micrófono");
            }
            else
            {
                MicrophoneMissing = true;
                Logger.Warn("Se pidió grabar el micrófono pero no hay ninguno conectado; se graba sin él.");
            }
        }

        foreach (var kind in new[] { StreamKind.AudioMix, StreamKind.AudioSystem, StreamKind.AudioMic }) _bus.ClearFormat(kind);
        CreateEncoders();

        _watcher.DefaultDeviceChanged += render =>
        {
            if (render) _system?.RequestReopen();
            else _microphone?.RequestReopen();
        };

        _thread = new Thread(Loop) { IsBackground = true, Name = "ReplayClip.Audio", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    /// <summary>True si se pidió micrófono y no había ninguno (para avisar al usuario).</summary>
    public bool MicrophoneMissing { get; }

    public bool HasAudio => _system is not null || _microphone is not null;

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(2000);
        _watcher.Dispose();
        DisposeEncoders();
        _system?.Dispose();
        _microphone?.Dispose();
        _stop.Dispose();
    }

    private void CreateEncoders()
    {
        int generation = _nextGeneration();
        bool both = _system is not null && _microphone is not null;
        if (_system is null && _microphone is null) return;
        _mixEncoder = new AacEncoder(StreamKind.AudioMix, generation, _bus.PublishFormat, Publish);
        if (both)
        {
            _systemEncoder = new AacEncoder(StreamKind.AudioSystem, generation, _bus.PublishFormat, Publish);
            _micEncoder = new AacEncoder(StreamKind.AudioMic, generation, _bus.PublishFormat, Publish);
        }
    }

    private void DisposeEncoders()
    {
        _mixEncoder?.Dispose();
        _systemEncoder?.Dispose();
        _micEncoder?.Dispose();
        _mixEncoder = _systemEncoder = _micEncoder = null;
    }

    private void Publish(in EncodedSampleInfo info, ReadOnlySpan<byte> data) => _bus.PublishSample(info, data);

    private void Loop()
    {
        using var mmcss = MmcssRegistration.Register("Audio");
        while (!_stop.Wait(10))
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Logger.Error("Error en la cadena de audio", ex);
                _stop.Wait(500);
            }
        }
    }

    private void Tick()
    {
        _system?.Poll();
        _microphone?.Poll();

        if ((_system?.Timeline.Discontinuity ?? false) || (_microphone?.Timeline.Discontinuity ?? false))
        {
            // Hueco enorme (suspensión): se empieza una generación de audio nueva.
            Logger.Info("Discontinuidad de audio; se reinician los codificadores AAC.");
            if (_system is not null) { _system.Timeline.Discontinuity = false; _system.Timeline.ClearPending(); }
            if (_microphone is not null) { _microphone.Timeline.Discontinuity = false; _microphone.Timeline.ClearPending(); }
            DisposeEncoders();
            _mixer.Reset();
            CreateEncoders();
            return;
        }

        if (_system is not null && _microphone is not null)
        {
            var sys = _system.Timeline;
            var mic = _microphone.Timeline;
            if (sys.PendingFrames > 0)
            {
                _systemEncoder!.Encode(sys.PendingStartIndex, sys.Pending);
                _mixer.PushSystem(sys.PendingStartIndex, sys.Pending);
                sys.ClearPending();
            }
            if (mic.PendingFrames > 0)
            {
                _micEncoder!.Encode(mic.PendingStartIndex, mic.Pending);
                _mixer.PushMicrophone(mic.PendingStartIndex, mic.Pending);
                mic.ClearPending();
            }
            if (_mixer.TryMix(out long start, out var mixed)) _mixEncoder!.Encode(start, mixed);
        }
        else
        {
            var only = (_system ?? _microphone)?.Timeline;
            if (only is { PendingFrames: > 0 })
            {
                _mixEncoder!.Encode(only.PendingStartIndex, only.Pending);
                only.ClearPending();
            }
        }
    }
}
