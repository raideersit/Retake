using ReplayClip.Core;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace ReplayClip.Audio;

/// <summary>
/// Codificador AAC-LC (48 kHz, estéreo, 160 kbps) con el MFT de Microsoft incluido en Windows.
/// Recibe PCM continuo con su índice en la línea de tiempo y publica tramas AAC (1024 muestras, ~21 ms).
/// </summary>
public sealed class AacEncoder : IDisposable
{
    public delegate void SampleCallback(in EncodedSampleInfo info, ReadOnlySpan<byte> data);

    /// <summary>160 kbps = 20000 bytes/s (valores admitidos: 12000, 16000, 20000, 24000).</summary>
    private const uint BytesPerSecond = 20_000;
    private const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);

    private readonly StreamKind _kind;
    private readonly int _generation;
    private readonly Action<StreamFormat> _onFormat;
    private readonly SampleCallback _onSample;
    private readonly IMFTransform _transform;
    private readonly int _outputBufferSize;
    private bool _formatPublished;
    private bool _disposed;

    public AacEncoder(StreamKind kind, int generation, Action<StreamFormat> onFormat, SampleCallback onSample)
    {
        _kind = kind;
        _generation = generation;
        _onFormat = onFormat;
        _onSample = onSample;
        _transform = CreateTransform();
        try
        {
            using (var output = MediaFactory.MFCreateMediaType())
            {
                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                output.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                output.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                output.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)PcmTimeline.SampleRate);
                output.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)PcmTimeline.Channels);
                output.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, BytesPerSecond);
                output.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);            // AAC crudo (lo que usa MP4)
                output.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29u); // AAC-LC
                _transform.SetOutputType(0, output, 0);
            }
            using (var input = MediaFactory.MFCreateMediaType())
            {
                input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                input.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                input.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                input.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)PcmTimeline.SampleRate);
                input.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)PcmTimeline.Channels);
                input.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(PcmTimeline.Channels * 2));
                input.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(PcmTimeline.SampleRate * PcmTimeline.Channels * 2));
                _transform.SetInputType(0, input, 0);
            }
            _outputBufferSize = Math.Max(_transform.GetOutputStreamInfo(0).Size, 8192);
            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        }
        catch
        {
            _transform.Dispose();
            throw;
        }
    }

    public StreamKind Kind => _kind;

    /// <summary>Codifica PCM intercalado que empieza en el índice <paramref name="startIndex"/> de la línea de tiempo.</summary>
    public unsafe void Encode(long startIndex, ReadOnlySpan<short> interleaved)
    {
        if (_disposed || interleaved.IsEmpty) return;
        int bytes = interleaved.Length * sizeof(short);
        int frames = interleaved.Length / PcmTimeline.Channels;

        using var buffer = MediaFactory.MFCreateMemoryBuffer(bytes);
        buffer.Lock(out IntPtr pointer, out _, out _);
        try
        {
            interleaved.CopyTo(new Span<short>((void*)pointer, interleaved.Length));
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = bytes;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = PcmTimeline.IndexToTicks(startIndex);
        sample.SampleDuration = PcmTimeline.IndexToTicks(startIndex + frames) - sample.SampleTime;
        _transform.ProcessInput(0, sample, 0);
        DrainOutputs();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
            DrainOutputs();
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        }
        catch (SharpGenException) { /* cierre: lo que no salga se pierde (< 50 ms) */ }
        _transform.Dispose();
    }

    private static IMFTransform CreateTransform()
    {
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Audio, GuidSubtype = AudioFormatGuids.Pcm };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Audio, GuidSubtype = AudioFormatGuids.Aac };
        uint flags = (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter);
        using var collection = MediaFactory.MFTEnumEx(TransformCategoryGuids.AudioEncoder, flags, input, output);
        foreach (var activate in collection)
        {
            return activate.ActivateObject<IMFTransform>();
        }
        throw new InvalidOperationException("No se encontró el codificador AAC de Windows (¿Windows N sin Media Feature Pack?).");
    }

    private unsafe void DrainOutputs()
    {
        while (true)
        {
            using var sample = MediaFactory.MFCreateSample();
            using (var outBuffer = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize))
            {
                sample.AddBuffer(outBuffer);
            }
            var output = new OutputDataBuffer { StreamID = 0, Sample = sample };
            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            output.Events?.Dispose();
            if (result.Code == MfETransformNeedMoreInput) return;
            result.CheckError();

            if (!_formatPublished) PublishFormat();
            using var buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out IntPtr pointer, out _, out int length);
            try
            {
                if (length > 0)
                {
                    var info = new EncodedSampleInfo(_kind, sample.SampleTime, sample.SampleDuration, true, _generation);
                    _onSample(info, new ReadOnlySpan<byte>((void*)pointer, length));
                }
            }
            finally
            {
                buffer.Unlock();
            }
        }
    }

    private void PublishFormat()
    {
        using var type = _transform.GetOutputCurrentType(0);
        _formatPublished = true;
        _onFormat(new StreamFormat(_kind, _generation, MediaTypeSerializer.Serialize(type), "AAC-LC 48 kHz estéreo 160 kbps"));
    }
}
