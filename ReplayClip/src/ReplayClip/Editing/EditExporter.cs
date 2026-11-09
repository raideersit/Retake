using ReplayClip.Core;
using ReplayClip.Muxing;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace ReplayClip.Editing;

/// <summary>
/// Exporta un montaje (<see cref="EditTimeline"/>) a MP4 H.264 + AAC. A diferencia de los clips normales aquí sí
/// se recodifica: recortes al fotograma exacto, uniones de archivos distintos y transiciones necesitan las imágenes
/// decodificadas. Se usa el SinkWriter de Media Foundation con el codificador por hardware si lo hay.
/// Solo se exporta el audio de la pista 1 (la mezcla de juego y micrófono).
/// </summary>
public static class EditExporter
{
    /// <summary>AAC 160 kbps, igual que los clips.</summary>
    private const uint AudioBytesPerSecond = 20_000;

    /// <summary>Exporta en un hilo aparte de prioridad baja. <paramref name="progress"/> recibe de 0 a 1.</summary>
    public static Task<string> ExportAsync(IReadOnlyList<EditSegment> segments, string folder, int bitrateMbps,
        IProgress<double>? progress, CancellationToken cancellation)
    {
        var copy = segments.Select(s => s.Clone()).ToList();
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(Export(copy, folder, bitrateMbps, progress, cancellation)); }
            catch (OperationCanceledException) { completion.SetCanceled(cancellation); }
            catch (Exception ex) { completion.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "ReplayClip.EditExporter",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
        return completion.Task;
    }

    public static string Export(IReadOnlyList<EditSegment> segments, string folder, int bitrateMbps,
        IProgress<double>? progress, CancellationToken cancellation)
    {
        var timeline = new EditTimeline(segments);
        if (timeline.Segments.Count == 0) throw new InvalidOperationException("No hay ningún tramo que exportar.");

        // Tamaño y ritmo de salida: los del primer tramo (pares, como exige H.264 4:2:0).
        var first = ClipReader.Probe(timeline.Segments[0].Path);
        int width = Math.Max(2, first.Width & ~1);
        int height = Math.Max(2, first.Height & ~1);
        int fps = first.FramesPerSecond >= 1 ? (int)Math.Clamp(Math.Round(first.FramesPerSecond), 15, 120) : 60;
        int bitrate = Math.Clamp(bitrateMbps, 1, 100) * 1_000_000;

        Directory.CreateDirectory(folder);
        double seconds = timeline.Duration;
        ClipExporter.EnsureFreeSpace(folder, (long)(seconds * (bitrate / 8.0 + AudioBytesPerSecond)));
        string path = ClipExporter.UniquePath(folder, $"Editado_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}", ".mp4");
        string partial = path + ".part";
        Logger.Info($"Editor: exportando {timeline.Segments.Count} tramos ({seconds:F1} s) a {width}x{height} {fps} fps → {path}");

        var readers = new ClipReader?[timeline.Segments.Count];
        try
        {
            using (var writer = OutputWriter.Create(partial, width, height, fps, bitrate))
            {
                long totalFrames = (long)Math.Ceiling(seconds * fps);
                var output = new byte[width * height * 4];
                var audioA = new short[ClipReader.SampleRate / Math.Min(fps, 15) * ClipReader.Channels];
                var audioB = new short[audioA.Length];
                var audioOut = new short[audioA.Length];

                for (long n = 0; n < totalFrames; n++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    double time = (double)n / fps;
                    var c = timeline.At(time);

                    // Abre los lectores al entrar cada tramo y cierra los que ya pasaron.
                    for (int i = 0; i < c.First; i++)
                    {
                        readers[i]?.Dispose();
                        readers[i] = null;
                    }
                    var a = readers[c.First] ??= Open(timeline.Segments[c.First], width, height);
                    var b = c.IsTransition ? readers[c.Second] ??= Open(timeline.Segments[c.Second], width, height) : null;

                    // Video
                    byte[] frameA = a.GetFrame(c.FirstSourceTime);
                    byte[]? frameB = b?.GetFrame(c.SecondSourceTime);
                    FrameMixer.ComposeVideo(output, frameA, frameB, width, height, c);
                    writer.WriteVideo(output, n);

                    // Audio del mismo intervalo [n, n+1) en muestras de 48 kHz.
                    long sampleStart = n * ClipReader.SampleRate / fps;
                    long sampleEnd = (n + 1) * ClipReader.SampleRate / fps;
                    int count = (int)(sampleEnd - sampleStart) * ClipReader.Channels;
                    var spanA = audioA.AsSpan(0, count);
                    var spanOut = audioOut.AsSpan(0, count);
                    a.ReadAudio(spanA);
                    if (b is null)
                    {
                        spanA.CopyTo(spanOut);
                    }
                    else
                    {
                        var spanB = audioB.AsSpan(0, count);
                        b.ReadAudio(spanB);
                        double overlap = timeline.OverlapAfter(c.First);
                        double progressEnd = c.Progress + 1.0 / fps / overlap;
                        FrameMixer.ComposeAudio(spanOut, spanA, spanB,
                            Composition.Weights(c.Kind, c.Progress), Composition.Weights(c.Kind, progressEnd), ClipReader.Channels);
                    }
                    writer.WriteAudio(spanOut, sampleStart);

                    if (n % 15 == 0) progress?.Report((double)n / totalFrames);
                }
                writer.Finish();
            }
            File.Move(partial, path);
            progress?.Report(1);
            Logger.Info($"Editor: exportado {path} ({new FileInfo(path).Length / 1024 / 1024} MB).");
            return path;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
        finally
        {
            foreach (var reader in readers) reader?.Dispose();
        }
    }

    private static ClipReader Open(EditSegment segment, int width, int height) =>
        new(segment.Path, segment.InSeconds, width, height);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>SinkWriter con una pista H.264 y una AAC, alimentadas con BGRA (o NV12) y PCM.</summary>
    private sealed class OutputWriter : IDisposable
    {
        private readonly IMFSinkWriter _writer;
        private readonly int _width, _height, _fps;
        private readonly bool _nv12;
        private readonly byte[]? _nv12Buffer;
        private const int VideoStream = 0;
        private const int AudioStream = 1;

        private OutputWriter(IMFSinkWriter writer, int width, int height, int fps, bool nv12)
        {
            _writer = writer;
            _width = width;
            _height = height;
            _fps = fps;
            _nv12 = nv12;
            if (nv12) _nv12Buffer = new byte[width * height * 3 / 2];
        }

        /// <summary>
        /// Prueba, por orden: codificador por hardware con entrada RGB32, hardware con NV12 (convertimos nosotros),
        /// y lo mismo por software. Algunos codificadores no aceptan RGB32 o no funcionan sin Direct3D.
        /// </summary>
        public static OutputWriter Create(string path, int width, int height, int fps, int bitrate)
        {
            Exception? last = null;
            foreach (bool hardware in new[] { true, false })
            {
                foreach (bool nv12 in new[] { false, true })
                {
                    TryDelete(path);
                    IMFSinkWriter? writer = null;
                    try
                    {
                        using var attributes = MediaFactory.MFCreateAttributes(3);
                        attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4);
                        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, hardware ? 1u : 0u);
                        attributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1u);
                        writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, attributes);
                        ConfigureVideo(writer, width, height, fps, bitrate, nv12);
                        ConfigureAudio(writer);
                        writer.BeginWriting();
                        Logger.Info($"Editor: codificador {(hardware ? "por hardware" : "por software")}, entrada {(nv12 ? "NV12" : "RGB32")}.");
                        return new OutputWriter(writer, width, height, fps, nv12);
                    }
                    catch (SharpGenException ex)
                    {
                        last = ex;
                        Logger.Warn($"Editor: no se pudo usar {(hardware ? "hardware" : "software")}/{(nv12 ? "NV12" : "RGB32")} ({ex.Message}).");
                        writer?.Dispose();
                    }
                }
            }
            throw new InvalidOperationException("No se pudo crear el codificador H.264 para exportar.", last);
        }

        private static void ConfigureVideo(IMFSinkWriter writer, int width, int height, int fps, int bitrate, bool nv12)
        {
            using (var output = MediaFactory.MFCreateMediaType())
            {
                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
                MediaFactory.MFSetAttributeSize(output, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
                MediaFactory.MFSetAttributeRatio(output, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1);
                MediaFactory.MFSetAttributeRatio(output, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                output.Set(MediaTypeAttributeKeys.InterlaceMode, 2u /* progresivo */);
                output.Set(MediaTypeAttributeKeys.Mpeg2Profile, Video.CodecApiGuids.H264ProfileHigh);
                int index = writer.AddStream(output);
                if (index != VideoStream) throw new InvalidOperationException("Índice de pista de video inesperado.");
            }
            using var input = MediaFactory.MFCreateMediaType();
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, nv12 ? VideoFormatGuids.NV12 : VideoFormatGuids.Rgb32);
            MediaFactory.MFSetAttributeSize(input, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
            MediaFactory.MFSetAttributeRatio(input, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1);
            MediaFactory.MFSetAttributeRatio(input, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
            input.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            // Paso positivo = imagen de arriba abajo (si no, RGB32 se interpretaría al revés).
            input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(nv12 ? width : width * 4));
            writer.SetInputMediaType(VideoStream, input, null!);
        }

        private static void ConfigureAudio(IMFSinkWriter writer)
        {
            using (var output = MediaFactory.MFCreateMediaType())
            {
                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                output.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                output.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                output.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)ClipReader.SampleRate);
                output.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)ClipReader.Channels);
                output.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, AudioBytesPerSecond);
                output.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
                output.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29u);
                int index = writer.AddStream(output);
                if (index != AudioStream) throw new InvalidOperationException("Índice de pista de audio inesperado.");
            }
            using var input = MediaFactory.MFCreateMediaType();
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            input.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            input.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            input.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)ClipReader.SampleRate);
            input.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)ClipReader.Channels);
            input.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(ClipReader.Channels * 2));
            input.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(ClipReader.SampleRate * ClipReader.Channels * 2));
            writer.SetInputMediaType(AudioStream, input, null!);
        }

        public void WriteVideo(byte[] bgra, long frameIndex)
        {
            ReadOnlySpan<byte> data = bgra.AsSpan(0, _width * _height * 4);
            if (_nv12)
            {
                FrameMixer.BgraToNv12(data, _nv12Buffer, _width, _height);
                data = _nv12Buffer;
            }
            long start = frameIndex * MediaClock.TicksPerSecond / _fps;
            long end = (frameIndex + 1) * MediaClock.TicksPerSecond / _fps;
            Write(VideoStream, data, start, end - start);
        }

        public void WriteAudio(ReadOnlySpan<short> pcm, long sampleIndex)
        {
            int frames = pcm.Length / ClipReader.Channels;
            long start = sampleIndex * MediaClock.TicksPerSecond / ClipReader.SampleRate;
            long end = (sampleIndex + frames) * MediaClock.TicksPerSecond / ClipReader.SampleRate;
            Write(AudioStream, System.Runtime.InteropServices.MemoryMarshal.AsBytes(pcm), start, end - start);
        }

        public void Finish() => _writer.Finalize();

        public void Dispose() => _writer.Dispose();

        private unsafe void Write(int stream, ReadOnlySpan<byte> data, long time, long duration)
        {
            using var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
            buffer.Lock(out IntPtr pointer, out _, out _);
            try { data.CopyTo(new Span<byte>((void*)pointer, data.Length)); }
            finally { buffer.Unlock(); }
            buffer.CurrentLength = data.Length;

            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = time;
            sample.SampleDuration = duration;
            _writer.WriteSample(stream, sample);
        }
    }
}
