using System.Runtime.InteropServices;
using ReplayClip.Core;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ReplayClip.Capture;

/// <summary>
/// Captura de un monitor completo con DXGI Desktop Duplication (IDXGIOutputDuplication), la API pública
/// que usa la "Captura de pantalla" de OBS. A diferencia de Windows.Graphics.Capture, Windows 10 no dibuja
/// el borde amarillo. No inyecta nada en el juego ni lee su memoria: recibe la imagen ya compuesta del
/// escritorio, en la GPU dueña del monitor.
/// <para>La duplicación no incluye el puntero del ratón: si se pide, se dibuja encima con GDI sobre una
/// copia de la textura en la GPU (solo una copia GPU→GPU por fotograma).</para>
/// <para>La duplicación se pierde con UAC, Ctrl+Alt+Supr o un cambio de modo de pantalla; se recrea sola
/// y mientras tanto el ritmo de fotogramas repite la última imagen.</para>
/// </summary>
public sealed class DesktopDuplicationSource : ICaptureSource
{
    private readonly D3DDeviceManager _device;
    private readonly long _pollInterval;
    private readonly CaptureFrameHandler _onFrame;
    private readonly bool _captureCursor;
    private readonly Dictionary<IntPtr, (int X, int Y)> _hotspots = new();
    private IDXGIOutput1? _output;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _cleanCopy;
    private ID3D11Texture2D? _composed;
    private Thread? _thread;
    private volatile bool _stop;
    private int _left, _top, _width, _height;
    private bool _hasFrame;
    private long _lastFrameTime;
    private DateTime _failingSince = DateTime.MaxValue;

    /// <param name="framesPerSecond">Fps de salida: se consulta la duplicación al doble de esa frecuencia.</param>
    public DesktopDuplicationSource(D3DDeviceManager device, int framesPerSecond, bool captureCursor, CaptureFrameHandler onFrame)
    {
        _device = device;
        _pollInterval = MediaClock.TicksPerSecond / framesPerSecond / 2;
        _captureCursor = captureCursor;
        _onFrame = onFrame;
    }

    public event Action<CaptureTarget>? TargetClosed;

    public CaptureTarget? CurrentTarget { get; private set; }

    public string MethodName => "Desktop Duplication (sin borde)";

    public void Start(CaptureTarget target)
    {
        if (!target.IsMonitor) throw new NotSupportedException("Desktop Duplication solo captura monitores.");
        Stop();

        _output = FindOutput(target.Handle)
                  ?? throw new NotSupportedException("El monitor no está conectado a la GPU del codificador.");
        var bounds = _output.Description.DesktopCoordinates;
        _left = bounds.Left;
        _top = bounds.Top;
        _width = bounds.Right - bounds.Left;
        _height = bounds.Bottom - bounds.Top;

        // Se crea aquí (no en el hilo) para que un fallo permita volver a WGC de inmediato.
        _duplication = CreateDuplication(_output);
        CurrentTarget = target;
        _stop = false;
        _hasFrame = false;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "ReplayClip.DesktopDuplication", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        Logger.Info($"Captura iniciada con Desktop Duplication: {target.Description} ({_width}x{_height}).");
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(2000);
        _thread = null;
        _duplication?.Dispose();
        _duplication = null;
        _output?.Dispose();
        _output = null;
        _cleanCopy?.Dispose();
        _cleanCopy = null;
        _composed?.Dispose();
        _composed = null;
        CurrentTarget = null;
    }

    public void Dispose() => Stop();

    private IDXGIOutput1? FindOutput(IntPtr monitor)
    {
        var adapter = _device.Adapter.Adapter;
        for (uint i = 0; adapter.EnumOutputs(i, out IDXGIOutput output).Success; i++)
        {
            if (output.Description.Monitor == monitor)
            {
                var output1 = output.QueryInterface<IDXGIOutput1>();
                output.Dispose();
                return output1;
            }
            output.Dispose();
        }
        return null;
    }

    private IDXGIOutputDuplication CreateDuplication(IDXGIOutput1 output)
    {
        // IDXGIOutput5 (Windows 10 1703+) permite pedir BGRA aunque el monitor esté en HDR o 10 bits.
        using var output5 = output.QueryInterfaceOrNull<IDXGIOutput5>();
        if (output5 is not null)
        {
            return output5.DuplicateOutput1(_device.Device, new[] { Format.B8G8R8A8_UNorm });
        }
        return output.DuplicateOutput(_device.Device);
    }

    private void CaptureLoop()
    {
        // AcquireNextFrame con espera retiene el contexto del dispositivo D3D11 mientras espera y frena al
        // codificador, que comparte el dispositivo. Por eso se consulta sin espera (timeout 0) a intervalos
        // cortos con un temporizador de alta resolución, igual que OBS.
        using var timer = new HighResolutionTimer();
        while (!_stop)
        {
            try
            {
                if (_duplication is null && !TryRecreate()) continue;
                AcquireOne();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Desktop Duplication: {ex.Message}");
                _duplication?.Dispose();
                _duplication = null;
                Thread.Sleep(250);
            }
            timer.Wait(_pollInterval);
        }
    }

    private bool TryRecreate()
    {
        try
        {
            _duplication = CreateDuplication(_output!);
            _failingSince = DateTime.MaxValue;
            Logger.Info("Desktop Duplication recuperada.");
            return true;
        }
        catch (SharpGenException ex)
        {
            // E_ACCESSDENIED durante UAC / pantalla de bloqueo; NOT_CURRENTLY_AVAILABLE si hay demasiadas duplicaciones.
            if (_failingSince == DateTime.MaxValue)
            {
                _failingSince = DateTime.UtcNow;
                Logger.Info($"Desktop Duplication no disponible por ahora (0x{ex.ResultCode.Code:X8}); se reintenta.");
            }
            else if (DateTime.UtcNow - _failingSince > TimeSpan.FromSeconds(5) && !MonitorStillExists())
            {
                var target = CurrentTarget;
                _stop = true;
                if (target is not null) TargetClosed?.Invoke(target);
            }
            Thread.Sleep(250);
            return false;
        }
    }

    private bool MonitorStillExists()
    {
        var handle = CurrentTarget?.Handle ?? IntPtr.Zero;
        return WindowEnumerator.GetMonitors().Any(m => m.Handle == handle);
    }

    private void AcquireOne()
    {
        var duplication = _duplication!;
        var result = duplication.AcquireNextFrame(0, out OutduplFrameInfo info, out IDXGIResource resource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout) return; // pantalla quieta: el ritmo repite la última imagen
        if (result == Vortice.DXGI.ResultCode.AccessLost)
        {
            Logger.Info("Desktop Duplication perdida (UAC, cambio de modo...); se recrea.");
            duplication.Dispose();
            _duplication = null;
            return;
        }
        result.CheckError();

        try
        {
            bool desktopChanged = info.LastPresentTime != 0;
            bool cursorMoved = info.LastMouseUpdateTime != 0;
            if (!desktopChanged && !(cursorMoved && _captureCursor && _hasFrame)) return;

            long time = desktopChanged ? MediaClock.QpcToTicks(info.LastPresentTime) : MediaClock.Now;
            if (time <= _lastFrameTime) time = MediaClock.Now;
            _lastFrameTime = time;

            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            if (!_captureCursor || !TryGetCursor(out var cursor))
            {
                if (desktopChanged)
                {
                    _hasFrame = true;
                    // Sin cursor: la textura de la duplicación va directa al conversor (cero copias).
                    _onFrame(texture, _width, _height, time);
                }
                return;
            }

            var composed = Compose(texture, desktopChanged, cursor);
            if (composed is not null) _onFrame(composed, _width, _height, time);
        }
        finally
        {
            resource.Dispose();
            duplication.ReleaseFrame();
        }
    }

    /// <summary>Copia el escritorio a una textura propia y dibuja el puntero encima.</summary>
    private ID3D11Texture2D? Compose(ID3D11Texture2D desktop, bool desktopChanged, CursorState cursor)
    {
        lock (_device.ContextLock)
        {
            if (_cleanCopy is null || _composed is null)
            {
                var description = desktop.Description;
                if (description.Format != Format.B8G8R8A8_UNorm) return desktopChanged ? desktop : null;
                description.Usage = ResourceUsage.Default;
                description.BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource;
                description.CPUAccessFlags = CpuAccessFlags.None;
                description.MipLevels = 1;
                description.ArraySize = 1;
                description.MiscFlags = ResourceOptionFlags.None;
                _cleanCopy = _device.Device.CreateTexture2D(description);
                description.MiscFlags = ResourceOptionFlags.GdiCompatible;
                _composed = _device.Device.CreateTexture2D(description);
            }

            if (desktopChanged)
            {
                _device.Context.CopyResource(_cleanCopy, desktop);
                _hasFrame = true;
            }
            _device.Context.CopyResource(_composed, _cleanCopy);

            using var surface = _composed.QueryInterface<IDXGISurface1>();
            IntPtr hdc = surface.GetDC(false);
            try
            {
                DrawIconEx(hdc, cursor.X - _left - cursor.HotspotX, cursor.Y - _top - cursor.HotspotY,
                    cursor.Handle, 0, 0, 0, IntPtr.Zero, DiNormal);
            }
            finally
            {
                surface.ReleaseDC(null);
            }
            return _composed;
        }
    }

    private readonly record struct CursorState(IntPtr Handle, int X, int Y, int HotspotX, int HotspotY);

    private bool TryGetCursor(out CursorState state)
    {
        state = default;
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || (info.Flags & CursorShowing) == 0 || info.Cursor == IntPtr.Zero) return false;
        if (info.X < _left || info.Y < _top || info.X >= _left + _width || info.Y >= _top + _height) return false;

        if (!_hotspots.TryGetValue(info.Cursor, out var hotspot))
        {
            if (GetIconInfo(info.Cursor, out IconInfo icon))
            {
                hotspot = (icon.XHotspot, icon.YHotspot);
                if (icon.MaskBitmap != IntPtr.Zero) DeleteObject(icon.MaskBitmap);
                if (icon.ColorBitmap != IntPtr.Zero) DeleteObject(icon.ColorBitmap);
            }
            if (_hotspots.Count > 64) _hotspots.Clear();
            _hotspots[info.Cursor] = hotspot;
        }
        state = new CursorState(info.Cursor, info.X, info.Y, hotspot.X, hotspot.Y);
        return true;
    }

    private const int CursorShowing = 0x00000001;
    private const int DiNormal = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool IsIcon;
        public int XHotspot;
        public int YHotspot;
        public IntPtr MaskBitmap;
        public IntPtr ColorBitmap;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int width, int height,
        int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
}
