using ReplayClip.Buffer;
using ReplayClip.Core;

namespace ReplayClip.Tests;

public class ReplayRingBufferTests
{
    private const long Second = MediaClock.TicksPerSecond;
    private const int Fps = 60;
    private const long FrameDuration = Second / Fps;
    private const long AacDuration = 1024L * Second / 48_000;

    /// <summary>Simula <paramref name="seconds"/> s de video (clave cada segundo) y audio AAC.</summary>
    private static void Feed(ReplayRingBuffer ring, double seconds, int videoBytes = 1000, int generation = 1,
        long startTicks = 0, bool audio = true)
    {
        var format = new StreamFormat(StreamKind.Video, generation, new byte[] { 1 }, "video");
        ring.OnFormat(format);
        ring.OnFormat(new StreamFormat(StreamKind.AudioMix, 1, new byte[] { 2 }, "aac"));
        var payload = new byte[videoBytes];
        long end = startTicks + (long)(seconds * Second);
        long audioPts = startTicks;
        for (long pts = startTicks, i = 0; pts < end; pts += FrameDuration, i++)
        {
            payload[0] = (byte)i;
            ring.OnSample(new EncodedSampleInfo(StreamKind.Video, pts, FrameDuration, i % Fps == 0, generation), payload);
            while (audio && audioPts <= pts)
            {
                ring.OnSample(new EncodedSampleInfo(StreamKind.AudioMix, audioPts, AacDuration, true, 1), new byte[400]);
                audioPts += AacDuration;
            }
        }
    }

    [Fact]
    public void Snapshot_starts_on_first_keyframe_inside_window_and_lasts_about_N_seconds()
    {
        using var ring = new ReplayRingBuffer(30, 1L << 30);
        Feed(ring, 45.5);

        using var snapshot = ring.CreateSnapshot();
        Assert.NotNull(snapshot);
        Assert.True(snapshot!.Video[0].Info.IsKeyFrame);
        Assert.InRange(snapshot.DurationSeconds, 29.0, 30.0);
        Assert.True(snapshot.StartPts >= snapshot.EndPts - 30 * Second);
    }

    [Fact]
    public void Snapshot_audio_is_trimmed_to_video_range()
    {
        using var ring = new ReplayRingBuffer(15, 1L << 30);
        Feed(ring, 40);

        using var snapshot = ring.CreateSnapshot()!;
        var audio = snapshot.Audio[StreamKind.AudioMix].Samples;
        Assert.All(audio, s => Assert.InRange(s.Info.Pts, snapshot.StartPts, snapshot.EndPts - 1));
        Assert.True(audio[0].Info.Pts - snapshot.StartPts < AacDuration);
    }

    [Fact]
    public void Short_buffer_returns_what_it_has()
    {
        using var ring = new ReplayRingBuffer(30, 1L << 30);
        Feed(ring, 5);
        using var snapshot = ring.CreateSnapshot()!;
        Assert.InRange(snapshot.DurationSeconds, 4.9, 5.1);
    }

    [Fact]
    public void Empty_buffer_returns_null()
    {
        using var ring = new ReplayRingBuffer(30, 1L << 30);
        Assert.Null(ring.CreateSnapshot());
    }

    [Fact]
    public void Old_samples_are_discarded_so_memory_does_not_grow()
    {
        using var ring = new ReplayRingBuffer(30, 1L << 30);
        Feed(ring, 60, videoBytes: 20_000);
        long afterOneMinute = ring.StoredBytes;
        long liveAfterOneMinute = ring.Pool.LiveBytes;

        Feed(ring, 600, videoBytes: 20_000, startTicks: 60 * Second); // 10 minutos más
        Assert.InRange(ring.StoredBytes, afterOneMinute * 95 / 100, afterOneMinute * 105 / 100);
        Assert.True(ring.Pool.LiveBytes <= liveAfterOneMinute + 2 * BlockPool.DefaultBlockSize);
        Assert.InRange(ring.BufferedVideoSeconds, 31.5, 32.5); // N + 2 s de margen
    }

    [Fact]
    public void Capacity_limit_drops_oldest_even_before_time_limit()
    {
        long capacity = 5L * 1024 * 1024;
        using var ring = new ReplayRingBuffer(120, capacity);
        Feed(ring, 60, videoBytes: 50_000, audio: false); // 3 MB/s, mucho más de lo que cabe

        Assert.True(ring.StoredBytes <= capacity);
        using var snapshot = ring.CreateSnapshot()!;
        Assert.True(snapshot.Video[0].Info.IsKeyFrame);
    }

    [Fact]
    public void Snapshot_keeps_its_data_while_the_buffer_keeps_writing()
    {
        using var ring = new ReplayRingBuffer(10, 1L << 30);
        Feed(ring, 20, videoBytes: 5000);
        var snapshot = ring.CreateSnapshot()!;
        byte firstByte = snapshot.Video[0].Data[0];
        long liveWithSnapshot = ring.Pool.LiveBytes;

        Feed(ring, 60, videoBytes: 5000, startTicks: 20 * Second); // el búfer descarta todo lo anterior
        Assert.Equal(firstByte, snapshot.Video[0].Data[0]);

        snapshot.Dispose();
        Assert.True(ring.Pool.LiveBytes < liveWithSnapshot + 2 * BlockPool.DefaultBlockSize);
    }

    [Fact]
    public void Snapshot_uses_only_latest_video_generation()
    {
        using var ring = new ReplayRingBuffer(30, 1L << 30);
        Feed(ring, 20, generation: 1);
        Feed(ring, 5, generation: 2, startTicks: 20 * Second);

        using var snapshot = ring.CreateSnapshot()!;
        Assert.All(snapshot.Video, s => Assert.Equal(2, s.Info.Generation));
        Assert.Equal(2, snapshot.VideoFormat.Generation);
        Assert.InRange(snapshot.DurationSeconds, 4.9, 5.1);
    }

    [Fact]
    public void Clear_releases_all_blocks()
    {
        var pool = new BlockPool();
        using var ring = new ReplayRingBuffer(30, 1L << 30, pool);
        Feed(ring, 30, videoBytes: 20_000);
        ring.Clear();
        Assert.Equal(0, ring.StoredBytes);
        Assert.True(pool.LiveBytes <= BlockPool.DefaultBlockSize); // solo el bloque de escritura actual
    }
}
