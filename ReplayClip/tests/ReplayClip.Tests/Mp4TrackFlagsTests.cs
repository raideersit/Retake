using ReplayClip.Muxing;

namespace ReplayClip.Tests;

public class Mp4TrackFlagsTests
{
    private static byte[] Box(string type, params byte[][] children)
    {
        int size = 8 + children.Sum(c => c.Length);
        var bytes = new List<byte> { (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size };
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
        foreach (var child in children) bytes.AddRange(child);
        return bytes.ToArray();
    }

    private static byte[] Raw(int length) => new byte[length];

    private static byte[] Track(string handler)
    {
        var tkhd = Box("tkhd", new byte[] { 0, 0, 0, 1 }, Raw(80));
        var hdlr = new List<byte> { 0, 0, 0, 0, 0, 0, 0, 0 };
        hdlr.AddRange(System.Text.Encoding.ASCII.GetBytes(handler));
        hdlr.AddRange(Raw(12));
        return Box("trak", tkhd, Box("mdia", Box("hdlr", hdlr.ToArray())));
    }

    private static List<int> TkhdFlags(byte[] file)
    {
        var flags = new List<int>();
        for (int i = 4; i + 8 < file.Length; i++)
        {
            if (file[i] == 't' && file[i + 1] == 'k' && file[i + 2] == 'h' && file[i + 3] == 'd')
            {
                flags.Add(file[i + 5] << 16 | file[i + 6] << 8 | file[i + 7]);
            }
        }
        return flags;
    }

    [Fact]
    public void Only_the_last_audio_track_stays_enabled()
    {
        var file = Box("ftyp", Raw(8)).Concat(Box("mdat", Raw(50)))
            .Concat(Box("moov", Track("vide"), Track("soun"), Track("soun"), Track("soun"))).ToArray();
        string path = Path.Combine(Path.GetTempPath(), $"flags_{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, file);
        try
        {
            Assert.Equal(3, Mp4TrackFlags.KeepOnlyLastAudioTrackEnabled(path));
            Assert.Equal(new List<int> { 1, 0, 0, 1 }, TkhdFlags(File.ReadAllBytes(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Single_audio_track_or_garbage_is_left_alone()
    {
        string path = Path.Combine(Path.GetTempPath(), $"flags_{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, Box("moov", Track("vide"), Track("soun")));
        try
        {
            Assert.Equal(1, Mp4TrackFlags.KeepOnlyLastAudioTrackEnabled(path));
            Assert.Equal(new List<int> { 1, 1 }, TkhdFlags(File.ReadAllBytes(path)));
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            Assert.Equal(0, Mp4TrackFlags.KeepOnlyLastAudioTrackEnabled(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
