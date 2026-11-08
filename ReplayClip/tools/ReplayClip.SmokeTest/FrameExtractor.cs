using System.Windows.Media.Imaging;
using Vortice.MediaFoundation;
using MediaClock = ReplayClip.Core.MediaClock;
using PixelFormats = System.Windows.Media.PixelFormats;

namespace ReplayClip.SmokeTest;

/// <summary>Decodifica un fotograma de un MP4 (Source Reader con conversión a RGB32) y lo guarda como PNG.</summary>
public static class FrameExtractor
{
    public static void SavePng(string mp4, double seconds, string png)
    {
        using var attributes = MediaFactory.MFCreateAttributes(2);
        attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(mp4, attributes);
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        using (var type = MediaFactory.MFCreateMediaType())
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, type);
        }
        using var current = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        MediaFactory.MFGetAttributeSize(current, MediaTypeAttributeKeys.FrameSize, out uint width, out uint height);

        long target = MediaClock.FromSeconds(seconds);
        while (true)
        {
            using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, 0, out _, out var flags, out long time);
            if ((flags & SourceReaderFlag.EndOfStream) != 0) throw new InvalidOperationException("El video terminó antes.");
            if (sample is null || time < target) continue;
            using var buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out IntPtr pointer, out _, out int length);
            try
            {
                int stride = (int)width * 4;
                var bitmap = BitmapSource.Create((int)width, (int)height, 96, 96, PixelFormats.Bgr32, null, pointer, length, stride);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(png);
                encoder.Save(file);
            }
            finally
            {
                buffer.Unlock();
            }
            Console.WriteLine($"Fotograma en {MediaClock.ToSeconds(time):F2} s guardado en {png} ({width}x{height}).");
            return;
        }
    }
}
