using ReplayClip.Core;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace ReplayClip.Editing;

/// <summary>Datos básicos de un archivo de video.</summary>
public readonly record struct ClipInfo(double DurationSeconds, int Width, int Height, double FramesPerSecond, bool HasAudio);

/// <summary>
/// Lee un tramo de un archivo de video desde <c>inSeconds</c>: fotogramas BGRA ya escalados al tamaño de salida
/// (lo hace el procesador de video de Media Foundation) y audio PCM 48 kHz estéreo de 16 bits. Usa dos
/// SourceReader independientes (uno para video y otro para audio) para que cada flujo avance a su ritmo.
/// Solo avanza hacia delante: el montaje se exporta de principio a fin.
/// </summary>
public sealed class ClipReader : IDisposable
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    private readonly int _width;
    private readonly int _height;
    private readonly double _inSeconds;
    private readonly IMFSourceReader _video;
    private readonly IMFSourceReader? _audio;
    private readonly int _defaultStride;

    private byte[] _current;
    private byte[] _next;
    private double _currentTime = double.NegativeInfinity;
    private double _nextTime = double.NaN;     // NaN = no hay siguiente cargado
    private bool _hasCurrent;
    private bool _videoEnded;

    private readonly AudioQueue _queue = new();
    private readonly LinearResampler? _resampler;
    private readonly int _audioChannels;
    private readonly int _audioRate;
    private bool _audioEnded;

    public ClipReader(string path, double inSeconds, int width, int height)
    {
        _width = width;
        _height = height;
        _inSeconds = inSeconds;
        _current = new byte[width * height * 4];
        _next = new byte[width * height * 4];

        using (var attributes = MediaFactory.MFCreateAttributes(2))
        {
            // Decodificación, conversión a RGB32 y escalado dentro de Media Foundation.
            attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 1u);
            _video = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
        }
        try
        {
            _video.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            _video.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
            using (var type = MediaFactory.MFCreateMediaType())
            {
                type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                MediaFactory.MFSetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
                MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                _video.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, type);
            }
            using (var current = _video.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
            {
                // Paso de fila si el búfer no expone IMF2DBuffer; negativo = imagen de abajo arriba.
                _defaultStride = unchecked((int)MediaFactory.MFGetAttributeUInt32(current, MediaTypeAttributeKeys.DefaultStride, (uint)(width * 4)));
            }
            if (inSeconds > 0) _video.SetCurrentPosition(ToTicks(inSeconds));

            _audio = TryOpenAudio(path, inSeconds, out _audioRate, out _audioChannels);
            if (_audio is not null && (_audioRate != SampleRate || _audioChannels != Channels))
            {
                _resampler = new LinearResampler(_audioRate, SampleRate, _audioChannels);
            }
            _audioEnded = _audio is null;
        }
        catch
        {
            _video.Dispose();
            _audio?.Dispose();
            throw;
        }
    }

    /// <summary>Duración, tamaño, fotogramas por segundo y si tiene audio.</summary>
    public static ClipInfo Probe(string path)
    {
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
        ulong duration = Convert.ToUInt64(reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration).Value);
        int width = 0, height = 0;
        double fps = 0;
        using (var type = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0))
        {
            MediaFactory.MFGetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, out uint w, out uint h);
            width = (int)w;
            height = (int)h;
            if (MediaFactory.MFGetAttributeRatio(type, MediaTypeAttributeKeys.FrameRate, out uint num, out uint den).Success && den != 0)
            {
                fps = (double)num / den;
            }
        }
        bool hasAudio;
        try
        {
            using var audio = reader.GetNativeMediaType(SourceReaderIndex.FirstAudioStream, 0);
            hasAudio = true;
        }
        catch (SharpGenException)
        {
            hasAudio = false;
        }
        return new ClipInfo(duration / (double)MediaClock.TicksPerSecond, width, height, fps, hasAudio);
    }

    /// <summary>
    /// Fotograma visible en el instante <paramref name="sourceSeconds"/> del archivo (el último cuya marca de tiempo
    /// no lo supera). Los instantes deben ir en aumento. Devuelve un búfer BGRA de arriba abajo, válido hasta la
    /// siguiente llamada; negro si todavía no hay ninguno.
    /// </summary>
    public byte[] GetFrame(double sourceSeconds)
    {
        // Margen de medio milisegundo para no perder fotogramas por redondeo de las marcas.
        double target = sourceSeconds + 0.0005;
        if (double.IsNaN(_nextTime) && !_videoEnded) ReadNextFrame();
        while (!double.IsNaN(_nextTime) && (_nextTime <= target || !_hasCurrent && _nextTime <= target + 0.5))
        {
            (_current, _next) = (_next, _current);
            _currentTime = _nextTime;
            _hasCurrent = true;
            _nextTime = double.NaN;
            if (!_videoEnded) ReadNextFrame();
        }
        if (!_hasCurrent) Array.Clear(_current);
        return _current;
    }

    /// <summary>Llena <paramref name="destination"/> con el audio que sigue (silencio si no hay o se acabó).</summary>
    public void ReadAudio(Span<short> destination)
    {
        while (_queue.Count < destination.Length && !_audioEnded) ReadAudioPacket();
        int taken = _queue.Take(destination);
        destination[taken..].Clear();
    }

    public void Dispose()
    {
        _video.Dispose();
        _audio?.Dispose();
    }

    private static long ToTicks(double seconds) => (long)Math.Round(seconds * MediaClock.TicksPerSecond);

    private IMFSourceReader? TryOpenAudio(string path, double inSeconds, out int rate, out int channels)
    {
        rate = SampleRate;
        channels = Channels;
        IMFSourceReader reader;
        try
        {
            reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
        }
        catch (SharpGenException)
        {
            return null;
        }
        try
        {
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
            try
            {
                SetPcmType(reader, SampleRate, Channels);
            }
            catch (SharpGenException)
            {
                // El decodificador no convierte la frecuencia: se pide su formato propio y se convierte aquí.
                using var native = reader.GetNativeMediaType(SourceReaderIndex.FirstAudioStream, 0);
                rate = (int)MediaFactory.MFGetAttributeUInt32(native, MediaTypeAttributeKeys.AudioSamplesPerSecond, SampleRate);
                channels = Math.Max(1, (int)MediaFactory.MFGetAttributeUInt32(native, MediaTypeAttributeKeys.AudioNumChannels, Channels));
                SetPcmType(reader, rate, channels);
            }
            if (inSeconds > 0) reader.SetCurrentPosition(ToTicks(inSeconds));
            return reader;
        }
        catch (SharpGenException ex)
        {
            Logger.Warn($"Editor: {Path.GetFileName(path)} sin audio utilizable ({ex.Message}).");
            reader.Dispose();
            return null;
        }
    }

    private static void SetPcmType(IMFSourceReader reader, int rate, int channels)
    {
        using var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        type.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
        type.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        type.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)rate);
        type.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels);
        type.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(channels * 2));
        type.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(rate * channels * 2));
        reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, type);
    }

    private unsafe void ReadNextFrame()
    {
        while (true)
        {
            using var sample = _video.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None,
                out _, out SourceReaderFlag flags, out long timestamp);
            if ((flags & (SourceReaderFlag.EndOfStream | SourceReaderFlag.Error)) != 0)
            {
                _videoEnded = true;
                return;
            }
            if (sample is null) continue; // marca de hueco o cambio de formato: se sigue leyendo

            using var buffer = sample.ConvertToContiguousBuffer();
            using var buffer2D = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
            int rowBytes = _width * 4;
            fixed (byte* destination = _next)
            {
                if (buffer2D is not null)
                {
                    buffer2D.Lock2D(out IntPtr scanline0, out int pitch);
                    try { CopyRows((byte*)scanline0, pitch, destination, rowBytes); }
                    finally { buffer2D.Unlock2D(); }
                }
                else
                {
                    buffer.Lock(out IntPtr pointer, out _, out _);
                    try
                    {
                        // Con paso negativo la primera fila de la imagen está al final del búfer.
                        byte* first = _defaultStride < 0 ? (byte*)pointer + (long)-_defaultStride * (_height - 1) : (byte*)pointer;
                        CopyRows(first, _defaultStride, destination, rowBytes);
                    }
                    finally { buffer.Unlock(); }
                }
            }
            _nextTime = timestamp / (double)MediaClock.TicksPerSecond;
            return;
        }
    }

    private unsafe void CopyRows(byte* source, int pitch, byte* destination, int rowBytes)
    {
        for (int y = 0; y < _height; y++)
        {
            System.Buffer.MemoryCopy(source + (long)y * pitch, destination + (long)y * rowBytes, rowBytes, rowBytes);
        }
    }

    private unsafe void ReadAudioPacket()
    {
        using var sample = _audio!.ReadSample(SourceReaderIndex.FirstAudioStream, SourceReaderControlFlag.None,
            out _, out SourceReaderFlag flags, out long timestamp);
        if ((flags & (SourceReaderFlag.EndOfStream | SourceReaderFlag.Error)) != 0)
        {
            _audioEnded = true;
            return;
        }
        if (sample is null) return;

        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out IntPtr pointer, out _, out int length);
        try
        {
            var samples = new ReadOnlySpan<short>((void*)pointer, length / 2);
            // Tras buscar, el primer paquete puede empezar antes del punto de entrada: se recorta.
            double start = timestamp / (double)MediaClock.TicksPerSecond;
            if (start < _inSeconds)
            {
                long skipFrames = (long)Math.Round((_inSeconds - start) * _audioRate);
                long skip = Math.Min(skipFrames * _audioChannels, samples.Length);
                samples = samples[(int)skip..];
            }
            if (samples.IsEmpty) return;
            if (_resampler is null) _queue.Add(samples);
            else _resampler.Process(samples, _queue);
        }
        finally
        {
            buffer.Unlock();
        }
    }

    /// <summary>Cola simple de muestras PCM intercaladas.</summary>
    internal sealed class AudioQueue
    {
        private short[] _data = new short[SampleRate * Channels];
        private int _start;

        public int Count { get; private set; }

        public void Add(ReadOnlySpan<short> samples)
        {
            if (_start + Count + samples.Length > _data.Length)
            {
                // Compacta y, si no basta, agranda.
                Array.Copy(_data, _start, _data, 0, Count);
                _start = 0;
                if (Count + samples.Length > _data.Length) Array.Resize(ref _data, Math.Max(_data.Length * 2, Count + samples.Length));
            }
            samples.CopyTo(_data.AsSpan(_start + Count));
            Count += samples.Length;
        }

        public int Take(Span<short> destination)
        {
            int n = Math.Min(destination.Length, Count);
            _data.AsSpan(_start, n).CopyTo(destination);
            _start += n;
            Count -= n;
            if (Count == 0) _start = 0;
            return n;
        }
    }

    /// <summary>
    /// Conversión sencilla de frecuencia (interpolación lineal) y de canales a estéreo. Solo se usa con archivos
    /// que no vienen de ReplayClip (que ya graba a 48 kHz estéreo).
    /// </summary>
    internal sealed class LinearResampler
    {
        private readonly double _step;
        private readonly int _channels;
        private double _position;              // posición fraccionaria respecto al primer fotograma del bloque actual
        private short _lastLeft, _lastRight;   // último fotograma del bloque anterior (posición -1)
        private bool _hasLast;
        private short[] _output = new short[4096];

        public LinearResampler(int inputRate, int outputRate, int channels)
        {
            _step = (double)inputRate / outputRate;
            _channels = channels;
        }

        public void Process(ReadOnlySpan<short> input, AudioQueue queue)
        {
            int frames = input.Length / _channels;
            if (frames == 0) return;
            int capacity = (int)((frames + 1) / _step) + 4;
            if (_output.Length < capacity * 2) _output = new short[capacity * 2];

            int produced = 0;
            if (!_hasLast)
            {
                (_lastLeft, _lastRight) = Frame(input, 0);
                _hasLast = true;
                _position = 0;
            }
            while (_position < frames - 1 + 1e-9 || _position < 0)
            {
                int index = (int)Math.Floor(_position);
                double fraction = _position - index;
                var (l0, r0) = index < 0 ? (_lastLeft, _lastRight) : Frame(input, index);
                var (l1, r1) = index + 1 < frames ? Frame(input, index + 1) : (l0, r0);
                _output[produced * 2] = (short)Math.Round(l0 + (l1 - l0) * fraction);
                _output[produced * 2 + 1] = (short)Math.Round(r0 + (r1 - r0) * fraction);
                produced++;
                _position += _step;
            }
            (_lastLeft, _lastRight) = Frame(input, frames - 1);
            _position -= frames;
            queue.Add(_output.AsSpan(0, produced * 2));
        }

        private (short Left, short Right) Frame(ReadOnlySpan<short> input, int index)
        {
            short left = input[index * _channels];
            short right = _channels > 1 ? input[index * _channels + 1] : left;
            return (left, right);
        }
    }
}
