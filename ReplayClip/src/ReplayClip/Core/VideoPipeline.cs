using ReplayClip.Capture;
using ReplayClip.Settings;
using ReplayClip.Video;
using Vortice.Direct3D11;

namespace ReplayClip.Core;

/// <summary>
/// Cadena de video de una sesión: dispositivo D3D11 → captura WGC → conversión NV12 → ritmo → H.264.
/// Todo ocurre en la GPU; a la CPU solo llegan los bytes ya comprimidos. Se recrea entera si se pierde
/// el dispositivo o cambian los ajustes de video.
/// </summary>
public sealed class VideoPipeline : IDisposable
{
    private readonly D3DDeviceManager _device;
    private readonly VideoFrameConverter _converter;
    private readonly H264Encoder _encoder;
    private readonly FramePacer _pacer;
    private readonly GraphicsCaptureSource _capture;
    private volatile bool _failed;
    private long _framesArrived;
    private long _framesAccepted;
    private long _framesNoSlot;
    private long _duplicates;
    private bool _disposed;

    public VideoPipeline(AppSettings settings, CaptureTarget? target, (int Width, int Height) outputSize,
        EncodedSampleBus bus, int generation)
    {
        IntPtr monitor = target?.Monitor ?? WindowEnumerator.GetMonitorOrPrimary(settings.MonitorDeviceName).Handle;
        _device = D3DDeviceManager.CreateForMonitor(monitor);
        try
        {
            int fps = settings.FramesPerSecond;
            var (width, height) = outputSize;
            int bitrate = settings.BitrateMbps * 1_000_000;
            H264Encoder? encoder = null;
            try
            {
                encoder = H264Encoder.Create(_device, new VideoEncoderSettings(width, height, fps, bitrate), generation,
                    bus.PublishFormat, bus.PublishSample, allowSoftware: false);
            }
            catch (InvalidOperationException)
            {
                // Sin codificador de hardware: el de software no puede con 1080p60 sin disparar la CPU.
                (width, height) = CaptureTargetResolver.ComputeOutputSize(width, height, 720);
                fps = 30;
                IsSoftwareFallback = true;
                Logger.Warn($"Sin codificador de hardware: se usa software a {width}x{height} {fps} fps.");
                encoder = H264Encoder.Create(_device, new VideoEncoderSettings(width, height, fps, bitrate), generation,
                    bus.PublishFormat, bus.PublishSample, allowSoftware: true);
            }
            _encoder = encoder;
            _encoder.Failed += _ => _failed = true;
            _converter = new VideoFrameConverter(_device, width, height);
            _pacer = new FramePacer(fps, OnDuplicateRequested);
            _capture = new GraphicsCaptureSource(_device, settings.CaptureCursor, OnFrameCaptured);
            _capture.TargetClosed += t => TargetClosed?.Invoke(t);
        }
        catch
        {
            _encoder?.Dispose();
            _device.Dispose();
            throw;
        }

        if (target is not null) SwitchTarget(target);
        else ShowBlack();
    }

    /// <summary>El objetivo de captura se cerró. Se invoca en un hilo del sistema.</summary>
    public event Action<CaptureTarget>? TargetClosed;

    public bool IsSoftwareFallback { get; }

    public CaptureTarget? CurrentTarget => _capture.CurrentTarget;

    /// <summary>Texto para la interfaz: "Intel Quick Sync (hardware) — 1920x1080 60 fps".</summary>
    public string Description =>
        $"{_encoder.Name} ({(_encoder.IsHardware ? "hardware" : "software")}, {(_encoder.UsesGpuInput ? "sin copias" : "con copia a CPU")}) — " +
        $"{_converter.OutputWidth}x{_converter.OutputHeight} {_pacer.FramesPerSecond} fps — GPU: {_device.Adapter.Name}";

    /// <summary>Contadores de diagnóstico (captura → codificador).</summary>
    public string Statistics =>
        $"capturados={Interlocked.Read(ref _framesArrived)} aceptados={Interlocked.Read(ref _framesAccepted)} " +
        $"sinTextura={Interlocked.Read(ref _framesNoSlot)} repetidos={Interlocked.Read(ref _duplicates)} | {_encoder.Statistics}";

    /// <summary>True si la cadena ya no funciona (dispositivo perdido o codificador caído).</summary>
    public bool IsBroken => _failed || _device.IsDeviceLost;

    /// <summary>Cambia de ventana o monitor sin reiniciar el codificador (la salida mantiene su tamaño).</summary>
    public void SwitchTarget(CaptureTarget target)
    {
        _converter.ResetInputCache();
        try
        {
            _capture.Start(target);
        }
        catch (Exception ex)
        {
            Logger.Error($"No se pudo capturar {target.Description}", ex);
            ShowBlack();
        }
    }

    /// <summary>Detiene la captura y emite imagen negra (objetivo cerrado o aún inexistente).</summary>
    public void ShowBlack()
    {
        _capture.Stop();
        var slot = _converter.ShowBlack(_encoder.IsSlotFree);
        if (slot is null) return;
        _encoder.Submit(slot, _pacer.ClaimPts(MediaClock.Now));
        _pacer.MarkSubmitted();
    }

    public void RequestKeyFrame() => _encoder.RequestKeyFrame();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _capture.Dispose();
        _pacer.Dispose();
        _encoder.Dispose();
        _converter.Dispose();
        _device.Dispose();
    }

    private void OnFrameCaptured(ID3D11Texture2D texture, int width, int height, long systemTime)
    {
        Interlocked.Increment(ref _framesArrived);
        if (_disposed || !_pacer.TryAccept(systemTime, out long pts)) return;
        Interlocked.Increment(ref _framesAccepted);
        var slot = _converter.Convert(texture, width, height, _encoder.IsSlotFree);
        if (slot is null)
        {
            Interlocked.Increment(ref _framesNoSlot); // codificador saturado: se descarta este fotograma
            return;
        }
        _encoder.Submit(slot, pts);
        _pacer.MarkSubmitted();
    }

    private void OnDuplicateRequested(long pts)
    {
        var slot = _converter.Latest;
        if (_disposed || slot is null) return;
        Interlocked.Increment(ref _duplicates);
        _encoder.Submit(slot, pts);
        _pacer.MarkSubmitted();
    }
}
