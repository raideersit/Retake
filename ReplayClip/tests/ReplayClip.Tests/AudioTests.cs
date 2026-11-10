using ReplayClip.Audio;
using ReplayClip.Settings;
using ReplayClip.Core;

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
    public void Late_packet_after_silence_fill_is_kept_not_dropped()
    {
        // El loopback (audio del juego) entrega con más retraso que el relleno de silencio:
        // antes se descartaba el paquete entero y solo quedaba el micrófono.
        var timeline = new PcmTimeline();
        timeline.FillSilenceUntil(48_000);
        timeline.FillSilenceUntil(48_000 + 9600);           // 200 ms de silencio ya emitidos
        timeline.ClearPending();
        timeline.Append(Ticks(48_000 + 4800), Frames(480, 9)); // paquete fechado 100 ms antes del final
        Assert.Equal(480, timeline.PendingFrames);
        Assert.True(timeline.Pending.ToArray().All(s => s == 9));

        // Los siguientes paquetes contiguos (igual de atrasados) tampoco se pierden.
        timeline.Append(Ticks(48_000 + 4800 + 480), Frames(480, 9));
        Assert.Equal(960, timeline.PendingFrames);
        Assert.Equal(48_000 + 9600 + 960, timeline.NextIndex);
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

public class AudioGainTests
{
    [Fact]
    public void Gain_one_returns_same_samples_without_copy()
    {
        short[] input = { 100, -200, 300 };
        var buffer = new short[1];
        var result = AudioGain.Apply(input, 1f, ref buffer);
        Assert.Equal(input, result.ToArray());
        Assert.Single(buffer);
    }

    [Fact]
    public void Gain_multiplies_and_saturates()
    {
        short[] input = { 1000, -1000, 20000, -20000 };
        var buffer = new short[1];
        var result = AudioGain.Apply(input, 3f, ref buffer);
        Assert.Equal(new short[] { 3000, -3000, short.MaxValue, short.MinValue }, result.ToArray());
    }

    [Fact]
    public void System_gain_setting_is_clamped()
    {
        Assert.Equal(100, new AppSettings { SystemGainPercent = 10 }.Normalize().SystemGainPercent);
        Assert.Equal(800, new AppSettings { SystemGainPercent = 5000 }.Normalize().SystemGainPercent);
        Assert.True(new AppSettings { SystemGainPercent = 300 }.RequiresPipelineRestart(new AppSettings()));
    }
}

public class GameDetectorTests
{
    private static readonly Dictionary<string, string> NoAliases = new();

    [Theory]
    [InlineData("League of Legends", "LOL")]
    [InlineData("WDC", "TWD")]
    [InlineData("wdc", "TWD")]
    public void Known_games_get_their_tag(string process, string expected)
    {
        Assert.Equal(expected, GameDetector.TagFor(process, null, NoAliases));
    }

    [Fact]
    public void Games_in_launcher_folders_use_the_process_name()
    {
        Assert.Equal("MYGAME", GameDetector.TagFor("MyGame", @"E:\SteamLibrary\steamapps\common\My Game\MyGame.exe", NoAliases));
    }

    [Fact]
    public void Ordinary_programs_are_not_games()
    {
        Assert.Null(GameDetector.TagFor("chrome", @"C:\Program Files\Google\Chrome\chrome.exe", NoAliases));
        Assert.Null(GameDetector.TagFor("explorer", @"C:\Windows\explorer.exe", NoAliases));
    }

    [Fact]
    public void User_aliases_win_and_are_sanitized()
    {
        var aliases = new Dictionary<string, string> { ["wdc"] = "twd 2!", ["blocnotas"] = "BN" };
        Assert.Equal("TWD2", GameDetector.TagFor("WDC", null, aliases));
        Assert.Equal("BN", GameDetector.TagFor("blocnotas", @"C:\Windows\notepad.exe", aliases));
    }

    [Fact]
    public void Clip_name_starts_with_the_tag()
    {
        Assert.Equal("LOL_Clip_", System.IO.Path.GetFileName(NameFor("LOL"))[..9]);
        Assert.StartsWith("Clip_", System.IO.Path.GetFileName(NameFor(null)));
    }

    private static string NameFor(string? tag)
    {
        string prefix = string.IsNullOrEmpty(tag) ? "" : tag + "_";
        return ReplayClip.Muxing.ClipExporter.UniquePath(System.IO.Path.GetTempPath(), $"{prefix}Clip_test_{Guid.NewGuid():N}", ".mp4");
    }
}
