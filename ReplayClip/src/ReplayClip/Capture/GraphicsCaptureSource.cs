using ReplayClip.Core;
using Vortice.Direct3D11;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace ReplayClip.Capture;

/// <summary>
/// Captura con Windows.Graphics.Capture (la API pública que usa la Barra de juegos): el compositor
/// de Windows (DWM) entrega cada fotograma como textura D3D11 en nuestro dispositivo. No toca el
/// proceso capturado: ni inyección, ni hooks, ni lectura de memoria.
/// </summary>
public sealed class GraphicsCaptureSource : IDisposable
{
    /// <summary>Firma del receptor de fotogramas. La textura solo es válida durante la llamada.</summary>
    public delegate void FrameHandler(ID3D11Texture2D texture, int contentWidth, int contentHeight, long systemTime);

    private readonly D3DDeviceManager _device;
    private readonly FrameHandler _onFrame;
    private readonly bool _captureCursor;
    private readonly object _gate = new();
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;
    private bool _disposed;

    public GraphicsCaptureSource(D3DDeviceManager device, bool captureCursor, FrameHandler onFrame)
    {
        _device = device;
        _captureCursor = captureCursor;
        _onFrame = onFrame;
    }

    /// <summary>El objetivo se cerró (ventana destruida o monitor desconectado).</summary>
    public event Action<CaptureTarget>? TargetClosed;

    public CaptureTarget? CurrentTarget { get; private set; }

    /// <summary>True si este Windows admite la captura (1903 o superior).</summary>
    public static bool IsSupported() => GraphicsCaptureSession.IsSupported();

    /// <summary>Empieza a capturar <paramref name="target"/>, sustituyendo el objetivo anterior.</summary>
    public void Start(CaptureTarget target)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopSessionLocked();

            var item = target.IsMonitor
                ? WinRtInterop.CreateItemForMonitor(target.Handle)
                : WinRtInterop.CreateItemForWindow(target.Handle);
            _item = item;
            _poolSize = item.Size;
            // Free-threaded: los fotogramas llegan en un hilo del sistema sin necesidad de bomba de mensajes.
            // Dos búferes son el mínimo para no bloquear al compositor.
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
            _framePool.FrameArrived += OnFrameArrived;
            _session = _framePool.CreateCaptureSession(item);
            ConfigureSession(_session);
            item.Closed += OnItemClosed;
            CurrentTarget = target;
            _session.StartCapture();
            Logger.Info($"Captura iniciada: {target.Description} ({_poolSize.Width}x{_poolSize.Height}).");
        }
    }

    /// <summary>Detiene la captura actual sin liberar el objeto.</summary>
    public void Stop()
    {
        lock (_gate) StopSessionLocked();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopSessionLocked();
        }
    }

    private void ConfigureSession(GraphicsCaptureSession session)
    {
        // Disponible desde Windows 10 2004 (build 19041). En 1903/1909 el cursor siempre se graba.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
            && ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
        {
            session.IsCursorCaptureEnabled = _captureCursor;
        }

        // Solo Windows 11 / Server 2022 (build 20348+). En Windows 10 el borde amarillo es obligatorio.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)
            && ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
        {
            try { session.IsBorderRequired = false; }
            catch (Exception ex) { Logger.Info($"El sistema no permitió quitar el borde de captura: {ex.Message}"); }
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;

            var contentSize = frame.ContentSize;
            using (var texture = WinRtInterop.GetTexture(frame.Surface))
            {
                // El contenido puede ser menor que el búfer del pool (la ventana encogió): se recorta.
                int width = Math.Min(contentSize.Width, _poolSize.Width);
                int height = Math.Min(contentSize.Height, _poolSize.Height);
                if (width > 0 && height > 0)
                {
                    _onFrame(texture, width, height, frame.SystemRelativeTime.Ticks);
                }
            }

            // Cambio de resolución (o la ventana creció): se recrea el pool con el tamaño nuevo.
            if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
            {
                lock (_gate)
                {
                    if (_framePool is null || contentSize.Width <= 0 || contentSize.Height <= 0) return;
                    _poolSize = contentSize;
                    _framePool.Recreate(_device.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
                    Logger.Info($"Tamaño de captura cambiado a {_poolSize.Width}x{_poolSize.Height}.");
                }
            }
        }
        catch (Exception ex)
        {
            // Típicamente el dispositivo se perdió; el motor lo detecta con IsDeviceLost y reinicia.
            Logger.Error("Error procesando un fotograma capturado", ex);
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        var target = CurrentTarget;
        Logger.Info($"El objetivo de captura se cerró: {target?.Description}");
        if (target is not null) TargetClosed?.Invoke(target);
    }

    private void StopSessionLocked()
    {
        if (_item is not null) _item.Closed -= OnItemClosed;
        if (_framePool is not null) _framePool.FrameArrived -= OnFrameArrived;
        _session?.Dispose();
        _framePool?.Dispose();
        _session = null;
        _framePool = null;
        _item = null;
        CurrentTarget = null;
    }
}
