using ReplayClip.Core;

namespace ReplayClip.Muxing;

/// <summary>
/// Deja habilitada en un MP4 solo la última pista de audio (la mezcla) y marca como deshabilitadas las demás
/// (sistema y micrófono). Media Foundation ignora esa bandera y elige la pista por su orden en el archivo
/// (por eso <see cref="Mp4Muxer"/> escribe la mezcla al final), pero los demás reproductores (navegadores,
/// VLC, ffmpeg...) reproducen la primera pista habilitada. Edita solo unos bytes del tkhd, en el sitio.
/// </summary>
public static class Mp4TrackFlags
{
    private const int TrackEnabled = 0x000001;

    /// <summary>Devuelve cuántas pistas de audio encontró. Nunca lanza: un fallo aquí no debe perder el clip.</summary>
    public static int KeepOnlyLastAudioTrackEnabled(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            if (!TryFindBox(file, 0, file.Length, "moov", out long moovStart, out long moovEnd)) return 0;
            long moovSize = moovEnd - moovStart;
            if (moovSize is <= 0 or > 64 * 1024 * 1024) return 0;
            var moov = new byte[moovSize];
            file.Position = moovStart;
            file.ReadExactly(moov);

            var audioFlagOffsets = new List<long>();
            foreach (var (type, start, end) in Children(moov, 0, moov.Length))
            {
                if (type != "trak") continue;
                int tkhd = -1;
                bool isAudio = false;
                foreach (var (childType, childStart, childEnd) in Children(moov, start, end))
                {
                    if (childType == "tkhd") tkhd = childStart;
                    else if (childType == "mdia")
                    {
                        foreach (var (mdiaChild, hdlrStart, _) in Children(moov, childStart, childEnd))
                        {
                            // hdlr: versión/flags (4) + pre_defined (4) + handler_type (4)
                            if (mdiaChild == "hdlr" && hdlrStart + 12 <= moov.Length) isAudio = moov[hdlrStart + 8] == 's' && moov[hdlrStart + 9] == 'o';
                        }
                    }
                }
                if (isAudio && tkhd >= 0) audioFlagOffsets.Add(moovStart + tkhd + 1);
            }

            if (audioFlagOffsets.Count < 2) return audioFlagOffsets.Count;
            for (int i = 0; i < audioFlagOffsets.Count; i++)
            {
                int flags = i == audioFlagOffsets.Count - 1 ? TrackEnabled : 0;
                file.Position = audioFlagOffsets[i];
                file.Write(new[] { (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags });
            }
            return audioFlagOffsets.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            Logger.Warn($"No se pudieron ajustar las pistas de audio de {path}: {ex.Message}");
            return 0;
        }
    }

    /// <summary>Busca una caja de primer nivel leyendo solo sus cabeceras.</summary>
    private static bool TryFindBox(FileStream file, long from, long to, string wanted, out long start, out long end)
    {
        start = end = 0;
        Span<byte> header = stackalloc byte[16];
        long position = from;
        while (position + 8 <= to)
        {
            file.Position = position;
            file.ReadExactly(header[..8]);
            long size = (uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]);
            string type = System.Text.Encoding.Latin1.GetString(header.Slice(4, 4));
            int headerSize = 8;
            if (size == 1)
            {
                file.ReadExactly(header.Slice(8, 8));
                size = 0;
                for (int i = 8; i < 16; i++) size = size << 8 | header[i];
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = to - position;
            }
            if (size < headerSize) return false;
            if (type == wanted)
            {
                start = position + headerSize;
                end = position + size;
                return end <= to;
            }
            position += size;
        }
        return false;
    }

    private static IEnumerable<(string Type, int Start, int End)> Children(byte[] buffer, int from, int to)
    {
        int position = from;
        while (position + 8 <= to)
        {
            long size = (uint)(buffer[position] << 24 | buffer[position + 1] << 16 | buffer[position + 2] << 8 | buffer[position + 3]);
            string type = System.Text.Encoding.Latin1.GetString(buffer, position + 4, 4);
            int headerSize = 8;
            if (size == 1) { yield break; } // cajas de 64 bits dentro de moov: no las usa MF
            if (size == 0) size = to - position;
            if (size < headerSize || position + size > to) yield break;
            yield return (type, position + headerSize, (int)(position + size));
            position += (int)size;
        }
    }
}
