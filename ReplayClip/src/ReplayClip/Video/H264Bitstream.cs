namespace ReplayClip.Video;

/// <summary>
/// Utilidades mínimas sobre H.264 en formato Annex-B (lo que emiten los MFT): localizar NAL,
/// detectar IDR y extraer SPS/PPS para el encabezado de secuencia del MP4.
/// </summary>
public static class H264Bitstream
{
    private const int NalIdr = 5;
    private const int NalSps = 7;
    private const int NalPps = 8;

    /// <summary>True si la muestra contiene un segmento IDR (fotograma clave).</summary>
    public static bool ContainsIdr(ReadOnlySpan<byte> data)
    {
        foreach (var (offset, _) in EnumerateNals(data))
        {
            if ((data[offset] & 0x1F) == NalIdr) return true;
        }
        return false;
    }

    /// <summary>
    /// Devuelve SPS y PPS con sus códigos de inicio (formato de MF_MT_MPEG_SEQUENCE_HEADER),
    /// o null si la muestra no los trae.
    /// </summary>
    public static byte[]? ExtractSequenceHeader(ReadOnlySpan<byte> data)
    {
        var header = new List<byte>();
        bool hasSps = false, hasPps = false;
        foreach (var (offset, length) in EnumerateNals(data))
        {
            int type = data[offset] & 0x1F;
            if (type is not (NalSps or NalPps)) continue;
            header.AddRange(new byte[] { 0, 0, 0, 1 });
            header.AddRange(data.Slice(offset, length).ToArray());
            hasSps |= type == NalSps;
            hasPps |= type == NalPps;
        }
        return hasSps && hasPps ? header.ToArray() : null;
    }

    /// <summary>Enumera (inicio, longitud) de cada NAL, sin el código de inicio.</summary>
    public static List<(int Offset, int Length)> EnumerateNals(ReadOnlySpan<byte> data)
    {
        var result = new List<(int, int)>();
        int start = -1;
        int i = 0;
        while (i + 3 <= data.Length)
        {
            int codeLength = 0;
            if (data[i] == 0 && data[i + 1] == 0)
            {
                if (data[i + 2] == 1) codeLength = 3;
                else if (i + 4 <= data.Length && data[i + 2] == 0 && data[i + 3] == 1) codeLength = 4;
            }
            if (codeLength == 0)
            {
                i++;
                continue;
            }
            if (start >= 0) result.Add((start, TrimTrailingZeros(data, start, i)));
            i += codeLength;
            start = i;
        }
        if (start >= 0 && start < data.Length) result.Add((start, data.Length - start));
        return result;
    }

    private static int TrimTrailingZeros(ReadOnlySpan<byte> data, int start, int end)
    {
        while (end > start && data[end - 1] == 0) end--;
        return end - start;
    }
}
