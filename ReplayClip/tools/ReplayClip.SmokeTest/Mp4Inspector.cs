using ReplayClip.Core;
using Vortice.MediaFoundation;

namespace ReplayClip.SmokeTest;

/// <summary>Resumen de una pista leída del MP4.</summary>
public sealed record TrackReport(int Index, string Type, int Samples, int KeyFrames, double FirstSeconds, double LastSeconds,
    double MaxKeyFrameGapSeconds);

/// <summary>Lee un MP4 con el Source Reader (sin decodificar) y resume sus pistas.</summary>
public static class Mp4Inspector
{
    public static (double DurationSeconds, List<TrackReport> Tracks) Inspect(string path)
    {
        using var attributes = MediaFactory.MFCreateAttributes(1);
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
        var durationVariant = reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration);
        double duration = MediaClock.ToSeconds(Convert.ToInt64(durationVariant.Value));

        var types = new List<string>();
        for (int i = 0; ; i++)
        {
            try
            {
                using var type = reader.GetNativeMediaType(i, 0);
                var subtype = type.GetGUID(MediaTypeAttributeKeys.Subtype);
                types.Add(subtype == VideoFormatGuids.H264 ? "H.264" : subtype == AudioFormatGuids.Aac ? "AAC" : subtype.ToString());
                reader.SetStreamSelection(i, true);
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
                break;
            }
        }

        var counts = new int[types.Count];
        var keys = new int[types.Count];
        var first = Enumerable.Repeat(double.NaN, types.Count).ToArray();
        var last = new double[types.Count];
        var lastKey = Enumerable.Repeat(double.NaN, types.Count).ToArray();
        var maxGap = new double[types.Count];

        while (true)
        {
            using var sample = reader.ReadSample(SourceReaderIndex.AnyStream, 0, out int stream, out var flags, out long timestamp);
            if ((flags & SourceReaderFlag.EndOfStream) != 0 || stream < 0) break;
            if (sample is null) continue;
            double t = MediaClock.ToSeconds(timestamp);
            counts[stream]++;
            if (double.IsNaN(first[stream])) first[stream] = t;
            last[stream] = t;
            if (sample.GetUInt32(SampleAttributeKeys.CleanPoint, out uint clean).Success && clean != 0)
            {
                keys[stream]++;
                if (!double.IsNaN(lastKey[stream])) maxGap[stream] = Math.Max(maxGap[stream], t - lastKey[stream]);
                lastKey[stream] = t;
            }
        }

        var tracks = types.Select((type, i) => new TrackReport(i, type, counts[i], keys[i], first[i], last[i], maxGap[i])).ToList();
        return (duration, tracks);
    }
}
