using System.Runtime.InteropServices;

namespace ReplayClip.Audio;

/// <summary>
/// Registra el hilo actual en MMCSS (Multimedia Class Scheduler) para que el audio no sufra cortes
/// cuando el juego satura la CPU. Es lo mismo que hace cualquier reproductor de audio de Windows.
/// </summary>
internal sealed class MmcssRegistration : IDisposable
{
    private IntPtr _handle;

    private MmcssRegistration(IntPtr handle) => _handle = handle;

    public static MmcssRegistration Register(string task)
    {
        uint index = 0;
        return new MmcssRegistration(AvSetMmThreadCharacteristicsW(task, ref index));
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) AvRevertMmThreadCharacteristics(_handle);
        _handle = IntPtr.Zero;
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll")]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
