using ReplayClip.Core;

namespace ReplayClip.Buffer;

/// <summary>Muestra guardada en el búfer: metadatos + ubicación de sus bytes.</summary>
public readonly record struct BufferedSample(EncodedSampleInfo Info, MemoryBlock Block, int Offset, int Length)
{
    public ReadOnlySpan<byte> Data => Block.Data.AsSpan(Offset, Length);
}

/// <summary>
/// Copia lógica de los últimos N segundos. No duplica bytes: retiene referencias a los bloques del pool,
/// así que el búfer puede seguir grabando mientras se exporta sin doblar el uso de RAM.
/// Hay que desecharla (Dispose) para devolver los bloques.
/// </summary>
public sealed class ReplaySnapshot : IDisposable
{
    private bool _disposed;

    internal ReplaySnapshot(StreamFormat videoFormat, IReadOnlyList<BufferedSample> video,
        IReadOnlyDictionary<StreamKind, (StreamFormat Format, IReadOnlyList<BufferedSample> Samples)> audio,
        long startPts, long endPts)
    {
        VideoFormat = videoFormat;
        Video = video;
        Audio = audio;
        StartPts = startPts;
        EndPts = endPts;
    }

    public StreamFormat VideoFormat { get; }

    /// <summary>Video desde el primer fotograma clave de la ventana; el primero siempre es IDR.</summary>
    public IReadOnlyList<BufferedSample> Video { get; }

    /// <summary>Pistas de audio presentes, en el orden de <see cref="StreamKind"/>.</summary>
    public IReadOnlyDictionary<StreamKind, (StreamFormat Format, IReadOnlyList<BufferedSample> Samples)> Audio { get; }

    /// <summary>PTS del primer fotograma (se convierte en el instante 0 del MP4).</summary>
    public long StartPts { get; }

    /// <summary>Fin del último fotograma.</summary>
    public long EndPts { get; }

    public double DurationSeconds => MediaClock.ToSeconds(EndPts - StartPts);

    /// <summary>Bytes totales a escribir (para comprobar el espacio libre en disco).</summary>
    public long TotalBytes =>
        Video.Sum(s => (long)s.Length) + Audio.Values.Sum(a => a.Samples.Sum(s => (long)s.Length));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var sample in Video) sample.Block.Release();
        foreach (var track in Audio.Values)
        {
            foreach (var sample in track.Samples) sample.Block.Release();
        }
    }
}
