using System.Runtime.InteropServices;

namespace ReplayClip.Capture;

/// <summary>
/// Espera precisa (≈0,5 ms) con un temporizador de alta resolución de Windows 10 1803+
/// (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION), sin subir la resolución global del reloj del sistema
/// como haría timeBeginPeriod (que aumenta el consumo de todo el equipo).
/// </summary>
public sealed class HighResolutionTimer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;

    private IntPtr _handle;

    public HighResolutionTimer()
    {
        _handle = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        // Windows anterior a 1803: temporizador normal (menos preciso, igual de válido).
        if (_handle == IntPtr.Zero) _handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);
    }

    /// <summary>Duerme <paramref name="ticks"/> unidades de 100 ns.</summary>
    public void Wait(long ticks)
    {
        if (ticks <= 0) return;
        long due = -ticks; // negativo = relativo
        if (_handle != IntPtr.Zero && SetWaitableTimerEx(_handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
        {
            WaitForSingleObject(_handle, 1000);
        }
        else
        {
            Thread.Sleep(TimeSpan.FromTicks(ticks));
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period, IntPtr completion,
        IntPtr completionArg, IntPtr wakeContext, uint tolerableDelay);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
