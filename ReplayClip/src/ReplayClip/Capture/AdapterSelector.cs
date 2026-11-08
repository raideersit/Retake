using ReplayClip.Core;
using Vortice;
using Vortice.DXGI;

namespace ReplayClip.Capture;

/// <summary>
/// Elige la GPU correcta. En portátiles con gráficos híbridos (Optimus/AMD switchable) la pantalla
/// la compone normalmente la GPU integrada aunque el juego use la dedicada. Si creamos el dispositivo
/// en el adaptador que posee el monitor, Windows.Graphics.Capture entrega la textura sin copias entre
/// GPUs, y el codificador de ese mismo adaptador (Quick Sync, NVENC o AMF) la lee directamente.
/// </summary>
public static class AdapterSelector
{
    /// <summary>Información del adaptador elegido.</summary>
    public sealed record AdapterChoice(IDXGIAdapter1 Adapter, string Name, uint VendorId, long Luid, bool OwnsMonitor);

    /// <summary>Devuelve el adaptador que tiene conectado <paramref name="monitor"/>, o el primero si no lo encuentra.</summary>
    public static AdapterChoice SelectForMonitor(IntPtr monitor)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? fallback = null;
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success; i++)
        {
            var desc = adapter.Description1;
            bool isSoftware = (desc.Flags & AdapterFlags.Software) != 0;
            if (!isSoftware && OwnsMonitor(adapter, monitor))
            {
                fallback?.Dispose();
                Logger.Info($"Adaptador elegido (dueño del monitor): {desc.Description} [vendor 0x{desc.VendorId:X4}]");
                return new AdapterChoice(adapter, desc.Description, desc.VendorId, ToInt64(desc.Luid), true);
            }
            if (fallback is null && !isSoftware) fallback = adapter;
            else adapter.Dispose();
        }

        if (fallback is null) throw new InvalidOperationException("No hay ningún adaptador gráfico disponible.");
        var fallbackDesc = fallback.Description1;
        Logger.Warn($"Ningún adaptador posee el monitor; se usa {fallbackDesc.Description} (puede haber copia entre GPUs).");
        return new AdapterChoice(fallback, fallbackDesc.Description, fallbackDesc.VendorId, ToInt64(fallbackDesc.Luid), false);
    }

    /// <summary>LUID empaquetado como en MFT_ENUM_ADAPTER_LUID (parte alta en los 32 bits superiores).</summary>
    private static long ToInt64(Luid luid) => ((long)luid.HighPart << 32) | luid.LowPart;

    private static bool OwnsMonitor(IDXGIAdapter1 adapter, IntPtr monitor)
    {
        for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
        {
            using (output)
            {
                if (output.Description.Monitor == monitor) return true;
            }
        }
        return false;
    }
}
