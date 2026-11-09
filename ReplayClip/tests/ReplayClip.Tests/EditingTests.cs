using ReplayClip.Editing;
using ReplayClip.UI;

namespace ReplayClip.Tests;

public class EditTimelineTests
{
    private static EditSegment Segment(double inS, double outS, TransitionKind kind = TransitionKind.Cut, double transition = 0.5) =>
        new("clip.mp4", 60) { InSeconds = inS, OutSeconds = outS, Transition = kind, TransitionSeconds = transition };

    [Fact]
    public void Cuts_concatenate_segments_back_to_back()
    {
        var timeline = new EditTimeline(new[] { Segment(2, 7), Segment(10, 13) });
        Assert.Equal(8, timeline.Duration, 6);
        Assert.Equal(5, timeline.StartOf(1), 6);

        var inFirst = timeline.At(1);
        Assert.Equal((0, 3.0, false), (inFirst.First, inFirst.FirstSourceTime, inFirst.IsTransition));
        var inSecond = timeline.At(6);
        Assert.Equal((1, 11.0, false), (inSecond.First, inSecond.FirstSourceTime, inSecond.IsTransition));
    }

    [Fact]
    public void Transition_overlaps_the_two_segments()
    {
        var timeline = new EditTimeline(new[] { Segment(0, 4, TransitionKind.Dissolve, 1), Segment(20, 24) });
        Assert.Equal(7, timeline.Duration, 6);        // 4 + 4 - 1 de solape
        Assert.Equal(3, timeline.StartOf(1), 6);

        var middle = timeline.At(3.5);
        Assert.True(middle.IsTransition);
        Assert.Equal((0, 1), (middle.First, middle.Second));
        Assert.Equal(3.5, middle.FirstSourceTime, 6);
        Assert.Equal(20.5, middle.SecondSourceTime, 6);
        Assert.Equal(0.5, middle.Progress, 6);
        Assert.Equal(0.5, middle.FirstWeight, 6);
        Assert.Equal(0.5, middle.SecondWeight, 6);

        Assert.False(timeline.At(4.1).IsTransition);
        Assert.Equal(1, timeline.At(4.1).First);
    }

    [Fact]
    public void Fade_through_black_is_dark_in_the_middle()
    {
        var timeline = new EditTimeline(new[] { Segment(0, 4, TransitionKind.FadeThroughBlack, 1), Segment(0, 4) });
        var middle = timeline.At(3.5);
        Assert.Equal(0, middle.FirstWeight, 6);
        Assert.Equal(0, middle.SecondWeight, 6);
        Assert.Equal(0.5, timeline.At(3.25).FirstWeight, 6);
        Assert.Equal(0.5, timeline.At(3.75).SecondWeight, 6);
    }

    [Fact]
    public void Transition_is_limited_to_half_of_the_shorter_segment()
    {
        var timeline = new EditTimeline(new[] { Segment(0, 10, TransitionKind.Dissolve, 3), Segment(0, 1) });
        Assert.Equal(0.5, timeline.OverlapAfter(0), 6);
        Assert.Equal(10.5, timeline.Duration, 6);
    }

    [Fact]
    public void Empty_and_tiny_segments_are_ignored()
    {
        Assert.Equal(0, new EditTimeline(Array.Empty<EditSegment>()).Duration);
        var timeline = new EditTimeline(new[] { Segment(0, 0.05), Segment(1, 3) });
        Assert.Single(timeline.Segments);
        Assert.Equal(2, timeline.Duration, 6);
    }
}

public class FrameMixerTests
{
    private static byte[] Solid(int pixels, byte value)
    {
        var frame = new byte[pixels * 4];
        Array.Fill(frame, value);
        return frame;
    }

    [Fact]
    public void Dissolve_blends_pixels()
    {
        var output = new byte[4 * 4];
        var c = new Composition(0, 0, 1, 0, TransitionKind.Dissolve, 0.5);
        FrameMixer.ComposeVideo(output, Solid(4, 200), Solid(4, 100), 2, 2, c);
        Assert.Equal(150, output[0]);
        Assert.Equal(255, output[3]); // alfa opaco
    }

    [Fact]
    public void Wipe_shows_second_frame_on_the_left()
    {
        var output = new byte[4 * 4];
        var c = new Composition(0, 0, 1, 0, TransitionKind.Wipe, 0.5);
        FrameMixer.ComposeVideo(output, Solid(4, 10), Solid(4, 90), 4, 1, c);
        Assert.Equal(new byte[] { 90, 90, 10, 10 }, new[] { output[0], output[4], output[8], output[12] });
    }

    [Fact]
    public void Audio_crossfade_ramps_between_weights()
    {
        var a = new short[] { 1000, 1000, 1000, 1000, 1000, 1000 };
        var b = new short[] { 0, 0, 0, 0, 0, 0 };
        var output = new short[6];
        FrameMixer.ComposeAudio(output, a, b, (1, 0), (0, 1), channels: 2);
        Assert.Equal(new short[] { 1000, 1000, 500, 500, 0, 0 }, output);
    }

    [Fact]
    public void Black_bgra_becomes_black_nv12()
    {
        var nv12 = new byte[2 * 2 * 3 / 2];
        FrameMixer.BgraToNv12(new byte[2 * 2 * 4], nv12, 2, 2);
        Assert.Equal(new byte[] { 16, 16, 16, 16, 128, 128 }, nv12);
    }
}

public class ClipReaderHelpersTests
{
    [Fact]
    public void Resampler_converts_rate_and_mono_to_stereo()
    {
        var queue = new ClipReader.AudioQueue();
        var resampler = new ClipReader.LinearResampler(24_000, 48_000, channels: 1);
        resampler.Process(new short[] { 0, 100, 200, 300 }, queue);
        resampler.Process(new short[] { 400, 500 }, queue);
        var output = new short[queue.Count];
        queue.Take(output);
        // Doble de fotogramas, estéreo, con los puntos intermedios interpolados y sin saltos entre bloques.
        Assert.Equal(new short[] { 0, 0, 50, 50, 100, 100, 150, 150, 200, 200, 250, 250, 300, 300, 350, 350, 400, 400, 450, 450, 500, 500 }, output);
    }

    [Fact]
    public void Audio_queue_keeps_order_across_growth()
    {
        var queue = new ClipReader.AudioQueue();
        var big = Enumerable.Range(0, 200_000).Select(i => (short)(i % 30_000)).ToArray();
        queue.Add(big.AsSpan(0, 1000));
        var first = new short[500];
        queue.Take(first);
        queue.Add(big.AsSpan(1000));
        var rest = new short[queue.Count];
        queue.Take(rest);
        Assert.Equal(big.Skip(500), rest);
    }
}

public class TimeTextTests
{
    [Theory]
    [InlineData("12.5", 12.5)]
    [InlineData("12,5", 12.5)]
    [InlineData("1:02.5", 62.5)]
    [InlineData(" 0:30 ", 30)]
    public void Parses_flexible_times(string text, double expected)
    {
        Assert.True(TimeText.TryParse(text, out double seconds));
        Assert.Equal(expected, seconds, 6);
    }

    [Fact]
    public void Formats_minutes_and_tenths()
    {
        Assert.Equal("1:02.5", TimeText.Format(62.5));
        Assert.False(TimeText.TryParse("abc", out _));
    }
}
