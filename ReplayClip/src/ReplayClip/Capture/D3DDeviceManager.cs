using ReplayClip.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using Windows.Graphics.DirectX.Direct3D11;

namespace ReplayClip.Capture;

/// <summary>
/// Dueño del dispositivo Direct3D 11 compartido por captura, conversión de color y codificador.
/// Expone el IMFDXGIDeviceManager que permite al codificador de hardware leer las texturas sin
/// pasarlas por la CPU. Si el dispositivo se pierde (driver reiniciado, GPU desconectada),
/// <see cref="IsDeviceLost"/> lo detecta y el motor recrea toda la cadena de video.
/// </summary>
public sealed class D3DDeviceManager : IDisposable
{
    private static readonly FeatureLevel[] FeatureLevels =
    {
        FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
    };

    private D3DDeviceManager(AdapterSelector.AdapterChoice adapter, ID3D11Device device, ID3D11DeviceContext context)
    {
        Adapter = adapter;
        Device = device;
        Context = context;
        VideoDevice = device.QueryInterface<ID3D11VideoDevice>();
        VideoContext = context.QueryInterface<ID3D11VideoContext>();

        // El codificador usa el contexto desde sus propios hilos: protección multihilo obligatoria.
        using (var multithread = device.QueryInterface<ID3D11Multithread>())
        {
            multithread.SetMultithreadProtected(true);
        }

        MfDeviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        MfDeviceManager.ResetDevice(device).CheckError();
        WinRtDevice = WinRtInterop.CreateWinRtDevice(device);
    }

    public AdapterSelector.AdapterChoice Adapter { get; }

    public ID3D11Device Device { get; }

    /// <summary>Contexto inmediato. Úsese dentro de <see cref="ContextLock"/>.</summary>
    public ID3D11DeviceContext Context { get; }

    public ID3D11VideoDevice VideoDevice { get; }

    public ID3D11VideoContext VideoContext { get; }

    /// <summary>Administrador de dispositivo DXGI de Media Foundation (para el MFT de hardware).</summary>
    public IMFDXGIDeviceManager MfDeviceManager { get; }

    /// <summary>El mismo dispositivo visto desde WinRT (para el frame pool de captura).</summary>
    public IDirect3DDevice WinRtDevice { get; }

    /// <summary>Serializa nuestro uso del contexto inmediato entre hilos.</summary>
    public object ContextLock { get; } = new();

    /// <summary>Crea el dispositivo en el adaptador que posee <paramref name="monitor"/>.</summary>
    public static D3DDeviceManager CreateForMonitor(IntPtr monitor)
    {
        var adapter = AdapterSelector.SelectForMonitor(monitor);
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var result = D3D11.D3D11CreateDevice(adapter.Adapter, DriverType.Unknown, flags, FeatureLevels,
            out ID3D11Device device, out FeatureLevel level, out ID3D11DeviceContext context);
        if (result.Failure)
        {
            adapter.Adapter.Dispose();
            throw new InvalidOperationException($"D3D11CreateDevice falló (0x{result.Code:X8}).");
        }
        Logger.Info($"Dispositivo D3D11 creado: {adapter.Name}, nivel {level}.");
        return new D3DDeviceManager(adapter, device, context);
    }

    /// <summary>True si el driver retiró el dispositivo (DXGI_ERROR_DEVICE_REMOVED y similares).</summary>
    public bool IsDeviceLost
    {
        get
        {
            try { return Device.DeviceRemovedReason.Failure; }
            catch (Exception) { return true; }
        }
    }

    public void Dispose()
    {
        (WinRtDevice as IDisposable)?.Dispose();
        MfDeviceManager.Dispose();
        VideoContext.Dispose();
        VideoDevice.Dispose();
        lock (ContextLock)
        {
            Context.ClearState();
            Context.Flush();
        }
        Context.Dispose();
        Device.Dispose();
        Adapter.Adapter.Dispose();
    }
}
