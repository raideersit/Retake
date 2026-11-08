using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ReplayClip.Capture;

/// <summary>Ventana de nivel superior candidata a captura.</summary>
public sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName, int ProcessId, int Width, int Height)
{
    public override string ToString() => $"{Title}  [{ProcessName}]";
}

/// <summary>Monitor conectado.</summary>
public sealed record MonitorInfo(IntPtr Handle, string DeviceName, int Width, int Height, bool IsPrimary)
{
    public override string ToString() => $"{DeviceName} ({Width}x{Height}){(IsPrimary ? " — principal" : "")}";
}

/// <summary>
/// Enumera ventanas y monitores con APIs de user32/dwmapi. Para conocer el nombre del proceso usa
/// la instantánea del sistema (Process.GetProcesses) y nunca abre un handle al proceso del juego:
/// así no hay nada que un antitrampas pueda interpretar como acceso a su memoria.
/// </summary>
public static class WindowEnumerator
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const int DwmwaCloaked = 14;

    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "ApplicationFrameWindow_Hidden", "NotifyIconOverflowWindow", "XamlExplorerHostIslandWindow",
    };

    /// <summary>Ventanas visibles con título, excluyendo las del propio ReplayClip.</summary>
    public static List<WindowInfo> GetCapturableWindows()
    {
        var names = Process.GetProcesses().ToDictionary(p => p.Id, p => SafeName(p));
        int ownPid = Environment.ProcessId;
        var result = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            var info = Describe(hwnd, names);
            if (info is not null && info.ProcessId != ownPid) result.Add(info);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Busca la ventana principal (la más grande) de un proceso por nombre.</summary>
    public static WindowInfo? FindProcessWindow(string processName, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        var candidates = GetCapturableWindows()
            .Where(w => string.Equals(w.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!string.IsNullOrEmpty(title))
        {
            var exact = candidates.FirstOrDefault(w => w.Title == title);
            if (exact is not null) return exact;
        }
        return candidates.OrderByDescending(w => (long)w.Width * w.Height).FirstOrDefault();
    }

    /// <summary>HWND en primer plano (barato: se puede consultar a menudo).</summary>
    public static IntPtr GetForegroundHandle() => GetForegroundWindow();

    /// <summary>
    /// Describe la ventana <paramref name="hwnd"/> si es capturable (no el escritorio, ni la barra, ni la nuestra).
    /// Más costoso que <see cref="GetForegroundHandle"/>: llamarlo solo cuando el HWND cambie.
    /// </summary>
    public static WindowInfo? DescribeIfCapturable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId) return null;
        var names = Process.GetProcesses().ToDictionary(p => p.Id, p => SafeName(p));
        return Describe(hwnd, names);
    }

    public static bool IsAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    public static bool IsMinimized(IntPtr hwnd) => IsIconic(hwnd);

    /// <summary>Monitor donde está la mayor parte de la ventana.</summary>
    public static IntPtr MonitorOfWindow(IntPtr hwnd) => MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);

    public static List<MonitorInfo> GetMonitors()
    {
        var result = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hmon, _, _, _) =>
        {
            var mi = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(hmon, ref mi))
            {
                result.Add(new MonitorInfo(hmon, mi.DeviceName, mi.Monitor.Right - mi.Monitor.Left,
                    mi.Monitor.Bottom - mi.Monitor.Top, (mi.Flags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Monitor por nombre de dispositivo; si no existe, el principal.</summary>
    public static MonitorInfo GetMonitorOrPrimary(string deviceName)
    {
        var monitors = GetMonitors();
        return monitors.FirstOrDefault(m => !string.IsNullOrEmpty(deviceName) && m.DeviceName == deviceName)
            ?? monitors.FirstOrDefault(m => m.IsPrimary)
            ?? monitors.FirstOrDefault()
            ?? throw new InvalidOperationException("No se detectó ningún monitor.");
    }

    private static WindowInfo? Describe(IntPtr hwnd, IReadOnlyDictionary<int, string> processNames)
    {
        if (!IsWindowVisible(hwnd)) return null;
        long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        if ((exStyle & WsExToolWindow) != 0 || (exStyle & WsExNoActivate) != 0) return null;
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return null;

        var className = new StringBuilder(256);
        GetClassName(hwnd, className, className.Capacity);
        if (ShellClasses.Contains(className.ToString())) return null;

        int length = GetWindowTextLength(hwnd);
        if (length == 0) return null;
        var title = new StringBuilder(length + 1);
        GetWindowText(hwnd, title, title.Capacity);

        if (!GetWindowRect(hwnd, out Rect rect)) return null;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (!IsIconic(hwnd) && (width < 50 || height < 50)) return null;

        GetWindowThreadProcessId(hwnd, out uint pid);
        string processName = processNames.TryGetValue((int)pid, out var n) ? n : "?";
        return new WindowInfo(hwnd, title.ToString(), processName, (int)pid, width, height);
    }

    private static string SafeName(Process p)
    {
        try { return p.ProcessName; }
        catch (InvalidOperationException) { return "?"; }
        finally { p.Dispose(); }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hmon, ref MonitorInfoEx info);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
