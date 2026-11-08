using System.Collections.Concurrent;
using ReplayClip.Capture;
using ReplayClip.Core;

namespace ReplayClip.Tests;

public class FramePacerTests
{
    [Fact]
    public void Static_image_is_repeated_at_full_frame_rate_with_even_timestamps()
    {
        var timestamps = new ConcurrentQueue<long>();
        using (var pacer = new FramePacer(60, pts => timestamps.Enqueue(pts)))
        {
            Assert.True(pacer.TryAccept(MediaClock.Now, out _)); // un único fotograma real
            pacer.MarkSubmitted();
            Thread.Sleep(1000);                                  // y después la imagen queda quieta
        }

        var list = timestamps.ToArray();
        Assert.InRange(list.Length, 55, 61);
        long interval = MediaClock.TicksPerSecond / 60;
        for (int i = 1; i < list.Length; i++) Assert.Equal(interval, list[i] - list[i - 1]);
    }

    [Fact]
    public void Extra_frames_from_high_refresh_monitor_are_dropped()
    {
        using var pacer = new FramePacer(60, _ => { });
        long start = MediaClock.Now;
        int accepted = 0;
        for (int i = 0; i < 144; i++) // 1 s de un monitor a 144 Hz
        {
            if (pacer.TryAccept(start + i * MediaClock.TicksPerSecond / 144, out _)) accepted++;
        }
        Assert.InRange(accepted, 59, 61);
    }

    [Fact]
    public void Timestamps_never_go_backwards()
    {
        using var pacer = new FramePacer(30, _ => { });
        long now = MediaClock.Now;
        Assert.True(pacer.TryAccept(now, out long first));
        long claimed = pacer.ClaimPts(now - MediaClock.TicksPerSecond); // marca anterior: se corrige
        Assert.True(claimed > first);
    }
}
