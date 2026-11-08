using System.Runtime.InteropServices;

namespace ReplayClip.SmokeTest;

/// <summary>
/// Conjunto de trabajo privado: la cifra de la columna "Memoria" del Administrador de tareas
/// (PROCESS_MEMORY_COUNTERS_EX2, Windows 10 1809+).
/// </summary>
public static class MemoryProbe
{
    public static long PrivateWorkingSetBytes()
    {
        var counters = new ProcessMemoryCountersEx2 { Size = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>() };
        return GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size) ? (long)counters.PrivateWorkingSetSize : -1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint Size;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
        public UIntPtr PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx2 counters, uint size);
}
