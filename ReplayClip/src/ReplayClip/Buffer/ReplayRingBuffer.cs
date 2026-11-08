using ReplayClip.Core;

namespace ReplayClip.Buffer;

/// <summary>
/// Búfer de repetición en RAM con muestras ya codificadas (H.264 y AAC) y sus marcas de tiempo.
/// <list type="bullet">
/// <item>Seguro entre hilos: el codificador de video y el de audio escriben a la vez; el exportador lee.</item>
/// <item>Conserva N+2 s (lo necesario para encontrar siempre un fotograma clave dentro de la ventana).</item>
/// <item>Tope duro de memoria: si se supera, se descarta lo más antiguo aunque no haya pasado el tiempo.</item>
/// </list>
/// </summary>
public sealed class ReplayRingBuffer : IEncodedSampleSink, IDisposable
{
    private static readonly StreamKind[] AllKinds = Enum.GetValues<StreamKind>();

    private readonly object _gate = new();
    private readonly BlockPool _pool;
    private readonly Dictionary<StreamKind, Queue<BufferedSample>> _queues = new();
    private readonly Dictionary<(StreamKind Kind, int Generation), StreamFormat> _formats = new();
    private MemoryBlock? _writeBlock;
    private long _retention;
    private long _capacityBytes;
    private long _storedBytes;
    private long _newestPts;
    private bool _disposed;

    public ReplayRingBuffer(int seconds, long capacityBytes, BlockPool? pool = null)
    {
        _pool = pool ?? new BlockPool();
        foreach (var kind in AllKinds) _queues[kind] = new Queue<BufferedSample>();
        Configure(seconds, capacityBytes);
    }

    /// <summary>Duración de repetición configurada (N).</summary>
    public int Seconds { get; private set; }

    /// <summary>Bytes de muestras guardadas (sin contar el hueco libre de los bloques).</summary>
    public long StoredBytes
    {
        get
        {
            lock (_gate) return _storedBytes;
        }
    }

    public BlockPool Pool => _pool;

    /// <summary>Segundos de video que hay ahora mismo en el búfer.</summary>
    public double BufferedVideoSeconds
    {
        get
        {
            lock (_gate)
            {
                var video = _queues[StreamKind.Video];
                return video.Count == 0 ? 0 : MediaClock.ToSeconds(_newestPts - video.Peek().Info.Pts);
            }
        }
    }

    /// <summary>Cambia N y el tope de memoria; recorta al instante si hace falta.</summary>
    public void Configure(int seconds, long capacityBytes)
    {
        lock (_gate)
        {
            Seconds = seconds;
            _retention = MediaClock.FromSeconds(seconds + 2);
            _capacityBytes = capacityBytes;
            _pool.MaxCachedBlocks = (int)Math.Max(4, capacityBytes / _pool.BlockSize + 2);
            TrimLocked();
        }
        _pool.TrimCache(_pool.MaxCachedBlocks);
    }

    public void OnFormat(StreamFormat format)
    {
        lock (_gate) _formats[(format.Kind, format.Generation)] = format;
    }

    public void OnSample(in EncodedSampleInfo info, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        lock (_gate)
        {
            if (_disposed) return;
            if (_writeBlock is null || _writeBlock.Remaining < data.Length)
            {
                _writeBlock?.Release(); // el búfer deja de escribir en él; siguen las referencias de sus muestras
                _writeBlock = _pool.Rent(data.Length);
            }
            var block = _writeBlock;
            int offset = block.Used;
            data.CopyTo(block.Data.AsSpan(offset));
            block.Used += data.Length;
            block.AddRef(); // referencia de la muestra
            _queues[info.Kind].Enqueue(new BufferedSample(info, block, offset, data.Length));
            _storedBytes += data.Length;
            if (info.Kind == StreamKind.Video) _newestPts = Math.Max(_newestPts, info.Pts + info.Duration);
            TrimLocked();
        }
    }

    /// <summary>
    /// Toma los últimos <paramref name="seconds"/> segundos. El clip empieza en el primer fotograma clave
    /// que cae dentro de la ventana; el audio se recorta a ese mismo intervalo. Null si aún no hay video.
    /// </summary>
    public ReplaySnapshot? CreateSnapshot(int? seconds = null)
    {
        lock (_gate)
        {
            var videoQueue = _queues[StreamKind.Video];
            if (videoQueue.Count == 0) return null;

            // Solo la generación más reciente: tras reiniciar el codificador, SPS/PPS pueden cambiar.
            int generation = videoQueue.Last().Info.Generation;
            if (!_formats.TryGetValue((StreamKind.Video, generation), out var videoFormat)) return null;

            var video = videoQueue.Where(s => s.Info.Generation == generation).ToList();
            long end = video[^1].Info.Pts + video[^1].Info.Duration;
            long windowStart = end - MediaClock.FromSeconds(seconds ?? Seconds);

            int startIndex = video.FindIndex(s => s.Info.IsKeyFrame && s.Info.Pts >= windowStart);
            if (startIndex < 0) startIndex = video.FindIndex(s => s.Info.IsKeyFrame); // búfer aún corto
            if (startIndex < 0) return null;
            video = video.GetRange(startIndex, video.Count - startIndex);
            long start = video[0].Info.Pts;

            var audio = new Dictionary<StreamKind, (StreamFormat, IReadOnlyList<BufferedSample>)>();
            foreach (var kind in AllKinds.Where(k => k != StreamKind.Video))
            {
                var queue = _queues[kind];
                if (queue.Count == 0) continue;
                int audioGeneration = queue.Last().Info.Generation;
                if (!_formats.TryGetValue((kind, audioGeneration), out var audioFormat)) continue;
                var samples = queue
                    .Where(s => s.Info.Generation == audioGeneration && s.Info.Pts >= start && s.Info.Pts < end)
                    .ToList();
                if (samples.Count > 0) audio[kind] = (audioFormat, samples);
            }

            foreach (var sample in video) sample.Block.AddRef();
            foreach (var (_, samples) in audio.Values)
            {
                foreach (var sample in samples) sample.Block.AddRef();
            }
            return new ReplaySnapshot(videoFormat, video, audio.ToDictionary(p => p.Key, p => p.Value), start, end);
        }
    }

    /// <summary>Vacía el búfer (p. ej. al cambiar la resolución de salida).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var queue in _queues.Values)
            {
                while (queue.Count > 0) queue.Dequeue().Block.Release();
            }
            _storedBytes = 0;
            _newestPts = 0;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Clear();
        lock (_gate)
        {
            _writeBlock?.Release();
            _writeBlock = null;
        }
        _pool.TrimCache(0);
    }

    private void TrimLocked()
    {
        if (_newestPts == 0) return;
        long limit = _newestPts - _retention;
        foreach (var queue in _queues.Values)
        {
            while (queue.Count > 0 && queue.Peek().Info.Pts < limit) RemoveOldest(queue);
        }

        // Tope de memoria: se sacrifica el video más antiguo y el audio anterior a él.
        var video = _queues[StreamKind.Video];
        bool trimmedForCapacity = false;
        while (_storedBytes > _capacityBytes && video.Count > 1)
        {
            RemoveOldest(video);
            trimmedForCapacity = true;
        }
        if (trimmedForCapacity && video.Count > 0)
        {
            long oldestVideo = video.Peek().Info.Pts;
            foreach (var kind in AllKinds.Where(k => k != StreamKind.Video))
            {
                var queue = _queues[kind];
                while (queue.Count > 0 && queue.Peek().Info.Pts < oldestVideo) RemoveOldest(queue);
            }
        }
    }

    private void RemoveOldest(Queue<BufferedSample> queue)
    {
        var sample = queue.Dequeue();
        _storedBytes -= sample.Length;
        sample.Block.Release();
    }
}
