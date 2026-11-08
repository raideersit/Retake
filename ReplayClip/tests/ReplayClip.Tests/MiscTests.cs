using System.Windows.Input;
using ReplayClip.Capture;
using ReplayClip.Hotkeys;
using ReplayClip.Settings;
using ReplayClip.Video;
using CaptureMode = ReplayClip.Settings.CaptureMode;

namespace ReplayClip.Tests;

public class H264BitstreamTests
{
    // SPS (tipo 7), PPS (tipo 8) e IDR (tipo 5) con códigos de inicio de 4 y 3 bytes.
    private static readonly byte[] KeyFrame =
    {
        0, 0, 0, 1, 0x67, 0x64, 0x00, 0x28,
        0, 0, 0, 1, 0x68, 0xEE, 0x3C, 0x80,
        0, 0, 1, 0x65, 0x88, 0x84, 0x00,
    };

    // Slice no IDR (tipo 1): first_mb_in_slice = 0 ("1"), slice_type = 5 → P ("00110").
    private static readonly byte[] PFrame = { 0, 0, 0, 1, 0x41, 0b1001_1000, 0x00 };

    // slice_type = 6 → B ("00111").
    private static readonly byte[] BFrame = { 0, 0, 0, 1, 0x01, 0b1001_1100, 0x00 };

    [Fact]
    public void Finds_all_nal_units()
    {
        var nals = H264Bitstream.EnumerateNals(KeyFrame);
        Assert.Equal(3, nals.Count);
        Assert.Equal(0x67, KeyFrame[nals[0].Offset]);
        Assert.Equal(0x65, KeyFrame[nals[2].Offset]);
    }

    [Fact]
    public void Detects_idr()
    {
        Assert.True(H264Bitstream.ContainsIdr(KeyFrame));
        Assert.False(H264Bitstream.ContainsIdr(PFrame));
    }

    [Fact]
    public void Extracts_sps_and_pps()
    {
        var header = H264Bitstream.ExtractSequenceHeader(KeyFrame);
        Assert.NotNull(header);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x67, 0x64, 0x00, 0x28, 0, 0, 0, 1, 0x68, 0xEE, 0x3C, 0x80 }, header);
        Assert.Null(H264Bitstream.ExtractSequenceHeader(PFrame));
    }

    [Fact]
    public void Reads_slice_types()
    {
        Assert.Equal('P', H264Bitstream.FirstSliceType(PFrame));
        Assert.Equal('B', H264Bitstream.FirstSliceType(BFrame));
    }
}

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Shift+F9", ModifierKeys.Control | ModifierKeys.Shift, Key.F9)]
    [InlineData("alt+F10", ModifierKeys.Alt, Key.F10)]
    [InlineData("F13", ModifierKeys.None, Key.F13)]
    public void Parses_valid_gestures(string text, ModifierKeys modifiers, Key key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(new HotkeyGesture(modifiers, key), gesture);
        Assert.True(HotkeyGesture.TryParse(gesture.ToString(), out var again));
        Assert.Equal(gesture, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("F9")]            // sin modificador: robaría la tecla al juego
    [InlineData("Ctrl+Shift")]    // falta la tecla
    [InlineData("Ctrl+F9+F10")]   // dos teclas
    [InlineData("Ctrl+Patata")]
    public void Rejects_invalid_gestures(string text) => Assert.False(HotkeyGesture.TryParse(text, out _));

    [Fact]
    public void Native_modifiers_match_RegisterHotKey()
    {
        HotkeyGesture.TryParse("Ctrl+Shift+F9", out var gesture);
        Assert.Equal(0x0002u | 0x0004u, gesture.NativeModifiers);
        Assert.Equal(0x78u, gesture.VirtualKey); // VK_F9
    }
}

public class SettingsTests
{
    [Fact]
    public void Normalize_clamps_out_of_range_values()
    {
        var settings = new AppSettings
        {
            ReplaySeconds = 500,
            BitrateMbps = 2,
            FramesPerSecond = 144,
            MaxOutputHeight = 2160,
            SaveReplayHotkey = "nada",
            OutputFolder = "",
        }.Normalize();

        Assert.Equal(120, settings.ReplaySeconds);
        Assert.Equal(8, settings.BitrateMbps);
        Assert.Equal(60, settings.FramesPerSecond);
        Assert.Equal(1080, settings.MaxOutputHeight);
        Assert.Equal("Ctrl+Shift+F9", settings.SaveReplayHotkey);
        Assert.Equal(AppSettings.DefaultOutputFolder, settings.OutputFolder);
    }

    [Fact]
    public void Buffer_estimate_for_default_settings_fits_target()
    {
        var settings = new AppSettings(); // 30 s a 20 Mbps
        Assert.InRange(settings.EstimatedBufferMegabytes(), 75, 85);
    }

    [Fact]
    public void Only_capture_changes_require_restart()
    {
        var a = new AppSettings();
        var b = a.Clone();
        b.ReplaySeconds = 60;
        b.SaveReplayHotkey = "Ctrl+Alt+F1";
        b.OutputFolder = @"D:\Clips";
        Assert.False(b.RequiresPipelineRestart(a));
        b.BitrateMbps = 10;
        Assert.True(b.RequiresPipelineRestart(a));
    }

    [Fact]
    public void Store_round_trips_and_survives_corrupt_file()
    {
        string path = Path.Combine(Path.GetTempPath(), $"replayclip-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings { ReplaySeconds = 45, CaptureMode = CaptureMode.Process, TargetProcessName = "League of Legends" });
            var loaded = store.Load();
            Assert.Equal(45, loaded.ReplaySeconds);
            Assert.Equal(CaptureMode.Process, loaded.CaptureMode);
            Assert.Equal("League of Legends", loaded.TargetProcessName);

            File.WriteAllText(path, "{ esto no es json");
            Assert.Equal(30, store.Load().ReplaySeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class GeometryTests
{
    [Theory]
    [InlineData(1920, 1080, 1080, 1920, 1080)]
    [InlineData(2560, 1440, 1080, 1920, 1080)]
    [InlineData(1366, 768, 1080, 1366, 768)]   // nunca se agranda
    [InlineData(3440, 1440, 1080, 1920, 802)]  // ultrapanorámico dentro de 1920x1080
    [InlineData(1920, 1080, 720, 1280, 720)]
    [InlineData(1281, 721, 1080, 1280, 720)]   // dimensiones pares
    public void Output_size_fits_box_and_is_even(int w, int h, int maxHeight, int expectedW, int expectedH)
    {
        var (width, height) = CaptureTargetResolver.ComputeOutputSize(w, h, maxHeight);
        Assert.Equal((expectedW, expectedH), (width, height));
    }

    [Fact]
    public void Letterbox_centers_and_keeps_aspect()
    {
        var rect = VideoFrameConverter.ComputeLetterbox(1280, 1024, 1920, 1080); // 5:4 dentro de 16:9
        Assert.Equal(1080, rect.Bottom - rect.Top);
        Assert.Equal(1350, rect.Right - rect.Left);
        Assert.Equal(284, rect.Left);
    }
}
