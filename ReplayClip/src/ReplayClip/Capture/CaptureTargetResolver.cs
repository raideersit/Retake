using ReplayClip.Settings;

namespace ReplayClip.Capture;

/// <summary>Lo que se está capturando: un monitor o una ventana.</summary>
/// <param name="IsMonitor">True = monitor completo.</param>
/// <param name="Handle">HMONITOR o HWND.</param>
/// <param name="Monitor">Monitor en el que está el objetivo (para elegir la GPU).</param>
/// <param name="Description">Texto para el registro y la interfaz.</param>
public sealed record CaptureTarget(bool IsMonitor, IntPtr Handle, IntPtr Monitor, string Description);

/// <summary>
/// Traduce el modo de captura de los ajustes a un objetivo concreto y decide cuándo cambiarlo:
/// la ventana en primer plano cambió, la ventana del juego se cerró y volvió a abrirse, etc.
/// Solo consulta APIs de user32 (GetForegroundWindow, EnumWindows): sin hooks de eventos.
/// </summary>
public sealed class CaptureTargetResolver
{
    private readonly AppSettings _settings;
    private IntPtr _lastForegroundSeen;

    public CaptureTargetResolver(AppSettings settings) => _settings = settings;

    /// <summary>Resuelve el objetivo actual, o null si todavía no existe (p. ej. el juego no está abierto).</summary>
    public CaptureTarget? Resolve()
    {
        switch (_settings.CaptureMode)
        {
            case CaptureMode.Monitor:
                var monitor = WindowEnumerator.GetMonitorOrPrimary(_settings.MonitorDeviceName);
                return new CaptureTarget(true, monitor.Handle, monitor.Handle, $"Monitor {monitor}");
            case CaptureMode.ForegroundWindow:
                _lastForegroundSeen = WindowEnumerator.GetForegroundHandle();
                return FromWindow(WindowEnumerator.DescribeIfCapturable(_lastForegroundSeen));
            case CaptureMode.Window:
                return FromWindow(WindowEnumerator.FindProcessWindow(_settings.TargetProcessName, _settings.TargetWindowTitle));
            case CaptureMode.Process:
                return FromWindow(WindowEnumerator.FindProcessWindow(_settings.TargetProcessName));
            default:
                return null;
        }
    }

    /// <summary>
    /// Llamado periódicamente. Devuelve un objetivo nuevo si hay que cambiar, o null si el actual sigue valiendo.
    /// </summary>
    public CaptureTarget? CheckForChange(CaptureTarget? current)
    {
        switch (_settings.CaptureMode)
        {
            case CaptureMode.Monitor:
                return current is null ? Resolve() : null;

            case CaptureMode.ForegroundWindow:
                IntPtr foreground = WindowEnumerator.GetForegroundHandle();
                if (foreground == _lastForegroundSeen) return null; // nada cambió: coste mínimo
                _lastForegroundSeen = foreground;
                if (current is not null && foreground == current.Handle) return null;
                // Si el primer plano no es capturable (escritorio, barra de tareas) se mantiene el actual.
                return FromWindow(WindowEnumerator.DescribeIfCapturable(foreground));

            default: // Window y Process: reenganchar si la ventana desapareció.
                if (current is not null && WindowEnumerator.IsAlive(current.Handle)) return null;
                return Resolve();
        }
    }

    /// <summary>
    /// Tamaño de referencia para fijar la resolución de salida al inicio de la sesión:
    /// el monitor del objetivo (o el principal). La salida no cambia aunque la ventana cambie de tamaño.
    /// </summary>
    public (int Width, int Height) GetReferenceSize(CaptureTarget? target)
    {
        var monitors = WindowEnumerator.GetMonitors();
        var monitor = target is null ? null : monitors.FirstOrDefault(m => m.Handle == target.Monitor);
        monitor ??= WindowEnumerator.GetMonitorOrPrimary(_settings.MonitorDeviceName);
        return (monitor.Width, monitor.Height);
    }

    /// <summary>
    /// Ajusta un tamaño de origen dentro de la caja máxima (1920x1080 o 1280x720) sin agrandarlo nunca,
    /// conservando la proporción y con dimensiones pares (requisito de NV12/H.264).
    /// </summary>
    public static (int Width, int Height) ComputeOutputSize(int sourceWidth, int sourceHeight, int maxHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return (maxHeight * 16 / 9, maxHeight);
        int maxWidth = maxHeight * 16 / 9;
        double scale = Math.Min(1.0, Math.Min(maxWidth / (double)sourceWidth, maxHeight / (double)sourceHeight));
        int width = Math.Max(2, (int)(sourceWidth * scale) & ~1);
        int height = Math.Max(2, (int)(sourceHeight * scale) & ~1);
        return (width, height);
    }

    private static CaptureTarget? FromWindow(WindowInfo? window) =>
        window is null ? null
            : new CaptureTarget(false, window.Handle, WindowEnumerator.MonitorOfWindow(window.Handle), $"Ventana {window}");
}
