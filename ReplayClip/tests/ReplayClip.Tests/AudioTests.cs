using ReplayClip.Audio;

namespace ReplayClip.Tests;

public class PcmTimelineTests
{
    private static short[] Frames(int count, short value)
    {
        var data = new short[count * PcmTimeline.Channels];
        Array.Fill(data, value);
        return data;
    }

    private static long Ticks(long index) => PcmTimeline.IndexToTicks(index);

    [Fact]
    public void Index_and_ticks_round_trip()
    {
        Assert.Equal(48_000, PcmTimeline.TicksToIndex(10_000_000));
        Assert.Equal(10_000_000, PcmTimeline.IndexToTicks(48_000));
    }

    [Fact]
    public void Contiguous_packets_are_appended_without_changes()
    {
        var timeline = new PcmTimeline();
        timeline.Append(Ticks(48_000), Frames(480, 7));
        timeline.Append(Ticks(48_480 + 100), Frames(480, 8)); // 2 ms de fluctuación: dentro de la tolerancia
        Assert.Equal(48_000, timeline.PendingStartIndex);
        Assert.Equal(960, timeline.PendingFrames);
        Assert.Equal(48_960, timeline.NextIndex);
    }

    [Fact]
    public void Gap_is_filled_with_silence()
    {
        var timeline = new PcmTimeline();
        timeline.Append(Ticks(0 + 48_000), Frames(480, 5));
        timeline.Append(Ticks(48_000 + 480 + 4800), Frames(480, 5)); // 100 ms después
        Assert.Equal(480 + 4800 + 480, timeline.PendingFrames);
        var pending = timeline.Pending;
        Assert.Equal(0, pending[480 * 2]);                 // silencio insertado
        Assert.Equal(5, pending[(480 + 4800) * 2]);        // el paquete llega a su sitio
    }

    [Fact]
    public void Overlap_is_dropped()
    {
        var timeline = new PcmTimeline();
        timeline.Append(Ticks(48_000), Frames(4800, 1));
        timeline.Append(Ticks(48_000 + 4800 - 2400), Frames(4800, 2)); // solapa 50 ms
        Assert.Equal(4800 + 2400, timeline.PendingFrames);
        Assert.Equal(48_000 + 4800 + 2400, timeline.NextIndex);
    }

    [Fact]
    public void Silence_fill_keeps_the_stream_moving()
    {
        var timeline = new PcmTimeline();
        timeline.FillSilenceUntil(10_000);   // primera llamada: solo fija el origen
        Assert.Equal(0, timeline.PendingFrames);
        timeline.FillSilenceUntil(10_960);
        Assert.Equal(960, timeline.PendingFrames);
        Assert.True(timeline.Pending.ToArray().All(s => s == 0));
    }

    [Fact]
    public void Huge_gap_is_skipped_and_flagged()
    {
        var timeline = new PcmTimeline();
        timeline.Append(Ticks(48_000), Frames(480, 1));
        timeline.ClearPending();
        timeline.FillSilenceUntil(48_480 + 48_000 * 10); // 10 s (p. ej. tras suspender)
        Assert.True(timeline.Discontinuity);
        Assert.Equal(0, timeline.PendingFrames);
    }
}

public class AudioMixerTests
{
    private static short[] Stereo(int frames, short value)
    {
        var data = new short[frames * 2];
        Array.Fill(data, value);
        return data;
    }

    [Fact]
    public void Mixes_only_the_common_range()
    {
        var mixer = new AudioMixer();
        mixer.PushSystem(1000, Stereo(100, 100));
        mixer.PushMicrophone(1050, Stereo(100, 10));

        Assert.True(mixer.TryMix(out long start, out var mixed));
        Assert.Equal(1050, start);
        Assert.Equal(50 * 2, mixed.Length);
        Assert.Equal(110, mixed[0]);
    }

    [Fact]
    public void Mixing_clamps_instead_of_wrapping()
    {
        var mixer = new AudioMixer();
        mixer.PushSystem(0, Stereo(10, 30_000));
        mixer.PushMicrophone(0, Stereo(10, 30_000));
        Assert.True(mixer.TryMix(out _, out var mixed));
        Assert.Equal(short.MaxValue, mixed[0]);
    }

    [Fact]
    public void Continues_where_it_left_off()
    {
        var mixer = new AudioMixer();
        mixer.PushSystem(0, Stereo(100, 1));
        mixer.PushMicrophone(0, Stereo(60, 1));
        Assert.True(mixer.TryMix(out long first, out var a));
        Assert.Equal((0L, 120), (first, a.Length));

        mixer.PushMicrophone(60, Stereo(40, 1));
        Assert.True(mixer.TryMix(out long second, out var b));
        Assert.Equal((60L, 80), (second, b.Length));
        Assert.False(mixer.TryMix(out _, out _));
    }
}
