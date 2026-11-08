using ReplayClip.Core;

namespace ReplayClip.Audio;

/// <summary>
/// Línea de tiempo continua de una fuente de audio a 48 kHz estéreo 16 bits, anclada al reloj QPC.
/// Convierte paquetes WASAPI (con huecos, solapes y deriva de reloj) en un flujo sin cortes:
/// <list type="bullet">
/// <item>Hueco de más de 20 ms (p. ej. el loopback no entrega nada cuando no suena nada): se rellena con silencio.</item>
/// <item>Solape de más de 20 ms: se descarta la parte repetida.</item>
/// <item>Si la fuente se calla o desaparece (micrófono desconectado), avanza con silencio para no desincronizar.</item>
/// </list>
/// </summary>
public sealed class PcmTimeline
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    /// <summary>Tolerancia antes de corregir: 20 ms.</summary>
    private const long ToleranceFrames = SampleRate / 50;

    private short[] _pending = new short[SampleRate / 10 * Channels];
    private int _pendingFrames;

    /// <summary>Índice absoluto (fotogramas de 48 kHz desde el origen del QPC) del próximo fotograma a emitir.</summary>
    public long NextIndex { get; private set; } = -1;

    /// <summary>Índice del primer fotograma pendiente de entregar.</summary>
    public long PendingStartIndex => NextIndex - _pendingFrames;

    public int PendingFrames => _pendingFrames;

    /// <summary>Se saltó un hueco grande: el flujo ya no es continuo con lo entregado antes.</summary>
    public bool Discontinuity { get; set; }

    public static long TicksToIndex(long ticks) => ticks * 3 / 625;      // ticks * 48000 / 10^7

    public static long IndexToTicks(long index) => index * 625 / 3;      // index * 10^7 / 48000

    /// <summary>Añade un paquete capturado en <paramref name="qpcTicks"/> (o -1 si la marca no es fiable).</summary>
    public void Append(long qpcTicks, ReadOnlySpan<short> interleaved)
    {
        int frames = interleaved.Length / Channels;
        if (frames == 0) return;
        long packetIndex = qpcTicks >= 0 ? TicksToIndex(qpcTicks) : NextIndex;
        if (NextIndex < 0) NextIndex = packetIndex < 0 ? TicksToIndex(MediaClock.Now) : packetIndex;
        if (packetIndex < 0) packetIndex = NextIndex;

        long difference = packetIndex - NextIndex;
        if (difference > ToleranceFrames)
        {
            AppendSilence(difference);
        }
        else if (difference < -ToleranceFrames)
        {
            int skip = (int)Math.Min(-difference, frames);
            interleaved = interleaved[(skip * Channels)..];
            frames -= skip;
        }
        if (frames > 0) AppendFrames(interleaved);
    }

    /// <summary>Avanza con silencio hasta <paramref name="targetIndex"/> si la fuente se quedó atrás.</summary>
    public void FillSilenceUntil(long targetIndex)
    {
        if (NextIndex < 0)
        {
            NextIndex = targetIndex;
            return;
        }
        if (targetIndex > NextIndex) AppendSilence(targetIndex - NextIndex);
    }

    /// <summary>Muestras pendientes (intercaladas) desde <see cref="PendingStartIndex"/>.</summary>
    public ReadOnlySpan<short> Pending => _pending.AsSpan(0, _pendingFrames * Channels);

    /// <summary>Marca como entregado todo lo pendiente.</summary>
    public void ClearPending() => _pendingFrames = 0;

    private void AppendSilence(long frames)
    {
        // Más de 2 s de hueco (p. ej. tras suspender el equipo): no se rellena; se salta y se avisa
        // con Discontinuity para que los codificadores empiecen una generación nueva.
        if (frames > SampleRate * 2)
        {
            _pendingFrames = 0;
            NextIndex += frames;
            Discontinuity = true;
            return;
        }
        EnsureCapacity((int)frames);
        _pending.AsSpan(_pendingFrames * Channels, (int)frames * Channels).Clear();
        _pendingFrames += (int)frames;
        NextIndex += frames;
    }

    private void AppendFrames(ReadOnlySpan<short> interleaved)
    {
        int frames = interleaved.Length / Channels;
        EnsureCapacity(frames);
        interleaved.CopyTo(_pending.AsSpan(_pendingFrames * Channels));
        _pendingFrames += frames;
        NextIndex += frames;
    }

    private void EnsureCapacity(int additionalFrames)
    {
        int needed = (_pendingFrames + additionalFrames) * Channels;
        if (needed <= _pending.Length) return;
        Array.Resize(ref _pending, Math.Max(needed, _pending.Length * 2));
    }
}
