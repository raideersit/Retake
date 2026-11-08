namespace ReplayClip.Buffer;

/// <summary>
/// Bloque de memoria con conteo de referencias. Lo referencian el búfer circular (mientras contiene
/// muestras suyas) y las instantáneas que se están exportando; vuelve al pool cuando nadie lo usa.
/// </summary>
public sealed class MemoryBlock
{
    private readonly BlockPool _owner;
    private int _references;

    internal MemoryBlock(byte[] data, BlockPool owner, bool reusable)
    {
        Data = data;
        _owner = owner;
        IsReusable = reusable;
    }

    public byte[] Data { get; }

    /// <summary>False para bloques de tamaño exacto (muestras mayores que un bloque normal).</summary>
    public bool IsReusable { get; }

    /// <summary>Bytes ya ocupados (solo lo modifica el escritor del búfer circular).</summary>
    public int Used { get; internal set; }

    public int Remaining => Data.Length - Used;

    internal void Reset()
    {
        Used = 0;
        _references = 1;
    }

    public void AddRef() => Interlocked.Increment(ref _references);

    public void Release()
    {
        int remaining = Interlocked.Decrement(ref _references);
        if (remaining == 0) _owner.Return(this);
        else if (remaining < 0) throw new InvalidOperationException("MemoryBlock liberado más veces de las referenciadas.");
    }
}

/// <summary>
/// Pool de bloques de 2 MB. Reutilizarlos evita que el recolector tenga que gestionar arreglos grandes
/// nuevos cada pocos segundos (CPU y picos de RAM). Las muestras mayores que un bloque (fotogramas clave
/// enormes) reciben un bloque exacto que no se reutiliza.
/// </summary>
public sealed class BlockPool
{
    public const int DefaultBlockSize = 2 * 1024 * 1024;

    private readonly Stack<MemoryBlock> _free = new();
    private readonly object _gate = new();
    private long _liveBytes;

    public BlockPool(int blockSize = DefaultBlockSize, int maxCachedBlocks = 64)
    {
        BlockSize = blockSize;
        MaxCachedBlocks = maxCachedBlocks;
    }

    public int BlockSize { get; }

    /// <summary>Máximo de bloques libres que se conservan para reutilizar.</summary>
    public int MaxCachedBlocks { get; set; }

    /// <summary>Bytes en bloques en uso (búfer + instantáneas), sin contar los libres.</summary>
    public long LiveBytes => Interlocked.Read(ref _liveBytes);

    /// <summary>Bytes reservados en total (en uso + libres en caché).</summary>
    public long ReservedBytes
    {
        get
        {
            lock (_gate) return LiveBytes + (long)_free.Count * BlockSize;
        }
    }

    /// <summary>Bloques libres en caché.</summary>
    public int CachedBlocks
    {
        get
        {
            lock (_gate) return _free.Count;
        }
    }

    /// <summary>Entrega un bloque con al menos <paramref name="minimumSize"/> bytes y una referencia.</summary>
    public MemoryBlock Rent(int minimumSize)
    {
        MemoryBlock block;
        if (minimumSize > BlockSize)
        {
            block = new MemoryBlock(new byte[minimumSize], this, reusable: false);
        }
        else
        {
            lock (_gate)
            {
                block = _free.Count > 0 ? _free.Pop() : new MemoryBlock(new byte[BlockSize], this, reusable: true);
            }
        }
        block.Reset();
        Interlocked.Add(ref _liveBytes, block.Data.Length);
        return block;
    }

    internal void Return(MemoryBlock block)
    {
        Interlocked.Add(ref _liveBytes, -block.Data.Length);
        if (!block.IsReusable) return;
        lock (_gate)
        {
            if (_free.Count < MaxCachedBlocks) _free.Push(block);
        }
    }

    /// <summary>Suelta bloques libres de la caché (p. ej. al reducir la duración del búfer).</summary>
    public void TrimCache(int keep)
    {
        lock (_gate)
        {
            while (_free.Count > keep) _free.Pop();
        }
    }
}
