using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ReplayClip.Capture;

/// <summary>
/// Puente entre Direct3D 11 (Vortice) y las APIs WinRT de captura. Usa solo interfaces públicas
/// documentadas (IGraphicsCaptureItemInterop, IDirect3DDxgiInterfaceAccess): no hay inyección ni hooks.
/// </summary>
internal static unsafe class WinRtInterop
{
    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IidId3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    /// <summary>Envuelve un ID3D11Device en el IDirect3DDevice de WinRT que pide el frame pool.</summary>
    public static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr inspectable));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>Crea el elemento de captura de una ventana (HWND).</summary>
    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd) => CreateItem(hwnd, forMonitor: false);

    /// <summary>Crea el elemento de captura de un monitor (HMONITOR).</summary>
    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmonitor) => CreateItem(hmonitor, forMonitor: true);

    /// <summary>
    /// Obtiene la textura D3D11 que hay detrás de una superficie de captura, sin copiarla.
    /// El llamador es dueño de la referencia devuelta (Dispose la libera).
    /// </summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        IntPtr unknown = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Guid iidAccess = IidDirect3DDxgiInterfaceAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref iidAccess, out IntPtr access));
            try
            {
                // IDirect3DDxgiInterfaceAccess::GetInterface es la entrada 3 de la vtable (tras IUnknown).
                var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(*(void***)access + 3));
                Guid iidTexture = IidId3D11Texture2D;
                IntPtr texture;
                Marshal.ThrowExceptionForHR(getInterface(access, &iidTexture, &texture));
                return new ID3D11Texture2D(texture);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static GraphicsCaptureItem CreateItem(IntPtr handle, bool forMonitor)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out IntPtr hstring));
        IntPtr factory;
        try
        {
            Guid iidInterop = IidGraphicsCaptureItemInterop;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref iidInterop, out factory));
        }
        finally
        {
            WindowsDeleteString(hstring);
        }

        try
        {
            // IGraphicsCaptureItemInterop: 3 = CreateForWindow, 4 = CreateForMonitor.
            int slot = forMonitor ? 4 : 3;
            var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(*(void***)factory + slot));
            Guid iidItem = IidGraphicsCaptureItem;
            IntPtr itemPointer;
            Marshal.ThrowExceptionForHR(create(factory, handle, &iidItem, &itemPointer));
            try
            {
                return GraphicsCaptureItem.FromAbi(itemPointer);
            }
            finally
            {
                Marshal.Release(itemPointer);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);
}
