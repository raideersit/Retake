namespace ReplayClip.Audio;

/// <summary>
/// Mezcla dos flujos continuos (sistema + micrófono) alineados por su índice en la línea de tiempo.
/// Solo emite el tramo que ya tienen ambas fuentes; como las dos avanzan con silencio cuando no
/// entregan nada, la mezcla nunca se queda atascada.
/// </summary>
public sealed class AudioMixer
{
    private readonly Fifo _a = new();
    private readonly Fifo _b = new();
    private short[] _mixed = new short[PcmTimeline.SampleRate / 10 * PcmTimeline.Channels];
    private long _nextIndex = -1;

    /// <summary>Ganancia del micrófono (1 = sin cambio).</summary>
    public float MicrophoneGain { get; set; } = 1.0f;

    public void PushSystem(long startIndex, ReadOnlySpan<short> samples) => _a.Push(startIndex, samples);

    public void PushMicrophone(long startIndex, ReadOnlySpan<short> samples) => _b.Push(startIndex, samples);

    /// <summary>Olvida todo (tras una discontinuidad).</summary>
    public void Reset()
    {
        _a.Clear();
        _b.Clear();
        _nextIndex = -1;
    }

    /// <summary>Entrega la mezcla disponible. Devuelve false si no hay nada nuevo.</summary>
    public bool TryMix(out long startIndex, out ReadOnlySpan<short> mixed)
    {
        startIndex = 0;
        mixed = default;
        if (_a.IsEmpty || _b.IsEmpty) return false;
        if (_nextIndex < 0) _nextIndex = Math.Max(_a.StartIndex, _b.StartIndex);
        _a.DropBefore(_nextIndex);
        _b.DropBefore(_nextIndex);

        long end = Math.Min(_a.EndIndex, _b.EndIndex);
        if (end <= _nextIndex || _a.StartIndex > _nextIndex || _b.StartIndex > _nextIndex) return false;

        int frames = (int)(end - _nextIndex);
        int count = frames * PcmTimeline.Channels;
        if (_mixed.Length < count) _mixed = new short[count];
        var a = _a.Slice(_nextIndex, frames);
        var b = _b.Slice(_nextIndex, frames);
        float gain = MicrophoneGain;
        for (int i = 0; i < count; i++)
        {
            int sum = a[i] + (int)(b[i] * gain);
            _mixed[i] = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
        }

        startIndex = _nextIndex;
        mixed = _mixed.AsSpan(0, count);
        _nextIndex = end;
        _a.DropBefore(_nextIndex);
        _b.DropBefore(_nextIndex);
        return true;
    }

    /// <summary>Cola de muestras con índice absoluto del primer fotograma.</summary>
    private sealed class Fifo
    {
        private short[] _data = new short[PcmTimeline.SampleRate * PcmTimeline.Channels];
        private int _frames;

        public long StartIndex { get; private set; } = -1;

        public long EndIndex => StartIndex + _frames;

        public bool IsEmpty => _frames == 0;

        public void Push(long startIndex, ReadOnlySpan<short> samples)
        {
            int frames = samples.Length / PcmTimeline.Channels;
            if (frames == 0) return;
            if (_frames == 0 || startIndex != EndIndex)
            {
                // Primer bloque o salto (no debería ocurrir: la línea de tiempo es continua).
                _frames = 0;
                StartIndex = startIndex;
            }
            // Límite de 1 s retenido: si la otra fuente no avanza, se descarta lo más viejo.
            int maxFrames = _data.Length / PcmTimeline.Channels;
            if (_frames + frames > maxFrames)
            {
                int drop = _frames + frames - maxFrames;
                DropBefore(StartIndex + drop);
                if (frames > maxFrames)
                {
                    samples = samples[((frames - maxFrames) * PcmTimeline.Channels)..];
                    StartIndex = startIndex + (frames - maxFrames);
                    frames = maxFrames;
                    _frames = 0;
                }
            }
            samples.CopyTo(_data.AsSpan(_frames * PcmTimeline.Channels));
            _frames += frames;
        }

        public ReadOnlySpan<short> Slice(long fromIndex, int frames) =>
            _data.AsSpan((int)(fromIndex - StartIndex) * PcmTimeline.Channels, frames * PcmTimeline.Channels);

        public void DropBefore(long index)
        {
            if (_frames == 0 || index <= StartIndex) return;
            int drop = (int)Math.Min(index - StartIndex, _frames);
            int remaining = _frames - drop;
            if (remaining > 0)
            {
                _data.AsSpan(drop * PcmTimeline.Channels, remaining * PcmTimeline.Channels).CopyTo(_data);
            }
            _frames = remaining;
            StartIndex += drop;
        }

        public void Clear()
        {
            _frames = 0;
            StartIndex = -1;
        }
    }
}
