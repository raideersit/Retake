using ReplayClip.Core;
using Vortice.MediaFoundation;

namespace ReplayClip.Muxing;

/// <summary>
/// Empaqueta muestras ya codificadas en MP4 con IMFSinkWriter en modo passthrough: el tipo de entrada
/// de cada pista es igual al de salida, así Media Foundation no inserta ningún codificador (no hay
/// recodificación ni FFmpeg). Admite MP4 normal (clips) y MP4 fragmentado (grabación continua, que sigue
/// siendo reproducible aunque la aplicación muera a mitad).
/// </summary>
public sealed class Mp4Muxer : IDisposable
{
    private readonly IMFSinkWriter _writer;
    private readonly Dictionary<StreamKind, int> _streamIndices = new();
    private readonly long _timeOffset;
    private bool _finalized;
    private bool _disposed;

    /// <param name="path">Archivo de salida (puede tener cualquier extensión; el contenedor se fuerza).</param>
    /// <param name="video">Formato de video (H.264).</param>
    /// <param name="audio">Pistas de audio AAC en orden.</param>
    /// <param name="timeOffset">PTS que se convierte en el instante 0 del archivo.</param>
    /// <param name="fragmented">True para MP4 fragmentado.</param>
    public Mp4Muxer(string path, StreamFormat video, IReadOnlyList<StreamFormat> audio, long timeOffset, bool fragmented)
    {
        _timeOffset = timeOffset;
        Path = path;
        using var attributes = MediaFactory.MFCreateAttributes(4);
        attributes.Set(TranscodeAttributeKeys.TranscodeContainertype,
            fragmented ? TranscodeContainerTypeGuids.Fmpeg4 : TranscodeContainerTypeGuids.Mpeg4);
        // Sin estrangulamiento: escribimos tan rápido como permita el disco.
        attributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1u);

        _writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, attributes);
        try
        {
            AddPassthroughStream(StreamKind.Video, video);
            foreach (var track in audio) AddPassthroughStream(track.Kind, track);
            _writer.BeginWriting();
        }
        catch
        {
            _writer.Dispose();
            throw;
        }
    }

    public string Path { get; }

    /// <summary>Pistas que contiene el archivo.</summary>
    public IReadOnlyCollection<StreamKind> Streams => _streamIndices.Keys;

    /// <summary>Escribe una muestra. Las pistas que no estén en el archivo se ignoran.</summary>
    public unsafe void WriteSample(in EncodedSampleInfo info, ReadOnlySpan<byte> data)
    {
        if (!_streamIndices.TryGetValue(info.Kind, out int streamIndex)) return;
        long time = info.Pts - _timeOffset;
        if (time < 0) return;

        using var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
        buffer.Lock(out IntPtr pointer, out _, out _);
        try
        {
            data.CopyTo(new Span<byte>((void*)pointer, data.Length));
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = data.Length;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = info.Duration;
        if (info.IsKeyFrame) sample.Set(SampleAttributeKeys.CleanPoint, 1u);
        _writer.WriteSample(streamIndex, sample);
    }

    /// <summary>Cierra el archivo (escribe el índice moov). Sin esto un MP4 normal no se puede reproducir.</summary>
    public void FinalizeFile()
    {
        if (_finalized) return;
        _finalized = true;
        _writer.Finalize();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer.Dispose();
    }

    private void AddPassthroughStream(StreamKind kind, StreamFormat format)
    {
        using var type = MediaTypeSerializer.Deserialize(format.MediaTypeBlob);
        int index = _writer.AddStream(type);
        _writer.SetInputMediaType(index, type, null!);
        _streamIndices[kind] = index;
    }
}
