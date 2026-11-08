using NAudio.Dsp;
using NAudio.Wave;

namespace ReplayClip.Audio;

/// <summary>
/// Respaldo cuando WASAPI no puede convertir por sí mismo (AUTOCONVERTPCM): pasa el formato de mezcla del
/// dispositivo (normalmente float 32 bits, 44,1/48/96 kHz, 2 a 8 canales) a 48 kHz estéreo 16 bits.
/// Usa el remuestreador WDL de NAudio (código gestionado, sin dependencias nativas).
/// </summary>
public sealed unsafe class PcmConverter
{
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    private readonly int _channels;
    private readonly int _bitsPerSample;
    private readonly bool _isFloat;
    private readonly int _inputRate;
    private readonly WdlResampler? _resampler;
    private float[] _stereo = Array.Empty<float>();
    private float[] _resampled = Array.Empty<float>();
    private short[] _output = Array.Empty<short>();

    public PcmConverter(WaveFormat format)
    {
        _channels = format.Channels;
        _bitsPerSample = format.BitsPerSample;
        _inputRate = format.SampleRate;
        _isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                   || (format is WaveFormatExtensible ext && ext.SubFormat == SubtypeFloat);
        bool isPcm = format.Encoding == WaveFormatEncoding.Pcm
                     || (format is WaveFormatExtensible ext2 && ext2.SubFormat == SubtypePcm);
        if (!_isFloat && !isPcm) throw new NotSupportedException($"Formato de audio no soportado: {format}");
        if (_isFloat && _bitsPerSample != 32) throw new NotSupportedException($"Float de {_bitsPerSample} bits no soportado.");

        if (_inputRate != PcmTimeline.SampleRate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(true);
            _resampler.SetRates(_inputRate, PcmTimeline.SampleRate);
        }
    }

    /// <summary>Convierte <paramref name="frames"/> fotogramas. El resultado es válido hasta la siguiente llamada.</summary>
    public ReadOnlySpan<short> Convert(IntPtr data, int frames, bool silent)
    {
        EnsureSize(ref _stereo, frames * 2);
        if (silent || data == IntPtr.Zero)
        {
            Array.Clear(_stereo, 0, frames * 2);
        }
        else
        {
            ToStereoFloat((byte*)data, frames);
        }

        float[] source = _stereo;
        int outFrames = frames;
        if (_resampler is not null)
        {
            int maxOut = (int)((long)frames * PcmTimeline.SampleRate / _inputRate) + 64;
            EnsureSize(ref _resampled, maxOut * 2);
            _resampler.ResamplePrepare(frames, 2, out float[] inBuffer, out int inOffset);
            Array.Copy(_stereo, 0, inBuffer, inOffset, frames * 2);
            outFrames = _resampler.ResampleOut(_resampled, 0, frames, maxOut, 2);
            source = _resampled;
        }

        EnsureSize(ref _output, outFrames * 2);
        for (int i = 0; i < outFrames * 2; i++)
        {
            float v = source[i] * 32767f;
            _output[i] = (short)(v > 32767f ? 32767 : v < -32768f ? -32768 : v);
        }
        return _output.AsSpan(0, outFrames * 2);
    }

    private void ToStereoFloat(byte* data, int frames)
    {
        int bytesPerSample = _bitsPerSample / 8;
        int frameBytes = bytesPerSample * _channels;
        for (int f = 0; f < frames; f++)
        {
            byte* frame = data + (long)f * frameBytes;
            float left = ReadSample(frame);
            float right = _channels > 1 ? ReadSample(frame + bytesPerSample) : left;
            _stereo[f * 2] = left;
            _stereo[f * 2 + 1] = right;
        }
    }

    private float ReadSample(byte* p)
    {
        if (_isFloat) return *(float*)p;
        return _bitsPerSample switch
        {
            16 => *(short*)p / 32768f,
            24 => ((p[0] << 8 | p[1] << 16 | p[2] << 24) >> 8) / 8388608f,
            32 => *(int*)p / 2147483648f,
            _ => 0f,
        };
    }

    private static void EnsureSize<T>(ref T[] array, int size)
    {
        if (array.Length < size) array = new T[Math.Max(size, array.Length * 2)];
    }
}
