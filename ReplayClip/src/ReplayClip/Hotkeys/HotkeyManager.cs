using System.Runtime.InteropServices;
using System.Windows.Interop;
using ReplayClip.Core;

namespace ReplayClip.Hotkeys;

/// <summary>
/// Atajos globales con RegisterHotKey sobre una ventana de solo mensajes.
/// RegisterHotKey NO es un hook de teclado: Windows entrega WM_HOTKEY sin que la aplicación vea
/// ninguna otra pulsación, por eso es seguro con antitrampas. Se usa siempre desde el hilo de UI.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 0x5200;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("ReplayClip.Hotkeys")
        {
            ParentWindow = HwndMessage,
            WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>Registra un atajo. Devuelve false si otra aplicación ya lo tiene.</summary>
    public bool TryRegister(HotkeyGesture gesture, Action handler, out int id)
    {
        id = _nextId++;
        if (!gesture.IsValid) return false;
        if (!RegisterHotKey(_source.Handle, id, gesture.NativeModifiers | ModNoRepeat, gesture.VirtualKey))
        {
            Logger.Warn($"No se pudo registrar el atajo {gesture} (error {Marshal.GetLastWin32Error()}); probablemente lo usa otra aplicación.");
            return false;
        }
        _handlers[id] = handler;
        Logger.Info($"Atajo registrado: {gesture}");
        return true;
    }

    /// <summary>Quita todos los atajos (antes de volver a registrar los de los ajustes nuevos).</summary>
    public void UnregisterAll()
    {
        foreach (int id in _handlers.Keys) UnregisterHotKey(_source.Handle, id);
        _handlers.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _handlers.TryGetValue(wParam.ToInt32(), out var handler))
        {
            handled = true;
            try { handler(); }
            catch (Exception ex) { Logger.Error("Error en el manejador de un atajo", ex); }
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
