using System.Diagnostics;

namespace ReplayClip.Core;

/// <summary>
/// Reloj único de la aplicación: el contador de rendimiento (QPC) expresado en unidades de 100 ns.
/// Es la misma base que usan Windows.Graphics.Capture (SystemRelativeTime) y WASAPI (qpcPosition),
/// por lo que video y audio quedan sincronizados sin conversiones adicionales.
/// </summary>
public static class MediaClock
{
    /// <summary>Unidades de 100 ns por segundo (la unidad de tiempo de Media Foundation).</summary>
    public const long TicksPerSecond = 10_000_000;

    private static readonly long Frequency = Stopwatch.Frequency;

    /// <summary>Instante actual en 100 ns del dominio QPC.</summary>
    public static long Now => QpcToTicks(Stopwatch.GetTimestamp());

    /// <summary>Convierte un valor crudo de QPC a 100 ns sin desbordar.</summary>
    public static long QpcToTicks(long qpc)
    {
        long seconds = qpc / Frequency;
        long remainder = qpc % Frequency;
        return seconds * TicksPerSecond + remainder * TicksPerSecond / Frequency;
    }

    /// <summary>Convierte segundos a 100 ns.</summary>
    public static long FromSeconds(double seconds) => (long)(seconds * TicksPerSecond);

    /// <summary>Convierte 100 ns a segundos.</summary>
    public static double ToSeconds(long ticks) => ticks / (double)TicksPerSecond;
}
