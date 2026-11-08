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

    /// <summary>
    /// Tipo del primer slice de la muestra: 'I', 'P', 'B' o '?' (lee first_mb_in_slice y slice_type, Exp-Golomb).
    /// Sirve para comprobar que el codificador no usa B-frames.
    /// </summary>
    public static char FirstSliceType(ReadOnlySpan<byte> data)
    {
        foreach (var (offset, length) in EnumerateNals(data))
        {
            int type = data[offset] & 0x1F;
            if (type is not (1 or NalIdr) || length < 2) continue;
            var reader = new BitReader(data.Slice(offset + 1, length - 1));
            reader.ReadUnsignedExpGolomb(); // first_mb_in_slice
            return (reader.ReadUnsignedExpGolomb() % 5) switch { 0 => 'P', 1 => 'B', 2 => 'I', _ => '?' };
        }
        return '?';
    }

    /// <summary>Lector de bits mínimo (ignora los bytes de prevención de emulación: basta para la cabecera).</summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _bit;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _bit = 0;
        }

        public uint ReadUnsignedExpGolomb()
        {
            int zeros = 0;
            while (ReadBit() == 0 && zeros < 31) zeros++;
            uint value = 0;
            for (int i = 0; i < zeros; i++) value = (value << 1) | (uint)ReadBit();
            return (1u << zeros) - 1 + value;
        }

        private int ReadBit()
        {
            if (_bit >= _data.Length * 8) return 1;
            int bit = (_data[_bit / 8] >> (7 - _bit % 8)) & 1;
            _bit++;
            return bit;
        }
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
