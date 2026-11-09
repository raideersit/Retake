namespace ReplayClip.Editing;

/// <summary>
/// Operaciones de imagen y sonido de las transiciones, sobre fotogramas BGRA de arriba abajo
/// (ancho × 4 bytes por fila, sin relleno) y PCM de 16 bits intercalado. Lógica pura, sin Media Foundation.
/// </summary>
public static class FrameMixer
{
    /// <summary>
    /// Escribe en <paramref name="output"/> la composición de dos fotogramas (<paramref name="second"/> puede ser
    /// null si no hay transición). Los pesos vienen de <see cref="Composition"/>.
    /// </summary>
    public static void ComposeVideo(byte[] output, byte[] first, byte[]? second, int width, int height, in Composition c)
    {
        if (!c.IsTransition || second is null)
        {
            System.Buffer.BlockCopy(first, 0, output, 0, width * height * 4);
            return;
        }
        if (c.Kind == TransitionKind.Wipe)
        {
            int edge = (int)Math.Round(width * c.Progress);
            int rowBytes = width * 4;
            for (int y = 0; y < height; y++)
            {
                int row = y * rowBytes;
                System.Buffer.BlockCopy(second, row, output, row, edge * 4);
                System.Buffer.BlockCopy(first, row + edge * 4, output, row + edge * 4, rowBytes - edge * 4);
            }
            return;
        }

        // Disolver y fundido a negro: suma ponderada en punto fijo (pesos de 0 a 256).
        int wa = (int)Math.Round(c.FirstWeight * 256);
        int wb = (int)Math.Round(c.SecondWeight * 256);
        int length = width * height * 4;
        for (int i = 0; i < length; i += 4)
        {
            output[i] = (byte)((first[i] * wa + second[i] * wb) >> 8);
            output[i + 1] = (byte)((first[i + 1] * wa + second[i + 1] * wb) >> 8);
            output[i + 2] = (byte)((first[i + 2] * wa + second[i + 2] * wb) >> 8);
            output[i + 3] = 255;
        }
    }

    /// <summary>Mezcla dos bloques de audio con los pesos de la composición, rampa lineal de <paramref name="startWeights"/> a <paramref name="endWeights"/>.</summary>
    public static void ComposeAudio(Span<short> output, ReadOnlySpan<short> first, ReadOnlySpan<short> second,
        (double First, double Second) startWeights, (double First, double Second) endWeights, int channels)
    {
        int frames = output.Length / channels;
        for (int f = 0; f < frames; f++)
        {
            double t = frames <= 1 ? 0 : (double)f / (frames - 1);
            double wa = startWeights.First + (endWeights.First - startWeights.First) * t;
            double wb = startWeights.Second + (endWeights.Second - startWeights.Second) * t;
            for (int ch = 0; ch < channels; ch++)
            {
                int i = f * channels + ch;
                double a = i < first.Length ? first[i] : 0;
                double b = i < second.Length ? second[i] : 0;
                output[i] = (short)Math.Clamp(Math.Round(a * wa + b * wb), short.MinValue, short.MaxValue);
            }
        }
    }

    /// <summary>
    /// Convierte BGRA a NV12 (BT.709, rango limitado). Respaldo por si el codificador no acepta RGB32 directamente.
    /// </summary>
    public static void BgraToNv12(ReadOnlySpan<byte> bgra, Span<byte> nv12, int width, int height)
    {
        int ySize = width * height;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int p = (y * width + x) * 4;
                int b = bgra[p], g = bgra[p + 1], r = bgra[p + 2];
                nv12[y * width + x] = (byte)Math.Clamp(((47 * r + 157 * g + 16 * b + 128) >> 8) + 16, 0, 255);
            }
        }
        for (int y = 0; y < height / 2; y++)
        {
            for (int x = 0; x < width / 2; x++)
            {
                // Media de los 4 píxeles del bloque 2×2.
                int r = 0, g = 0, b = 0;
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int p = ((y * 2 + dy) * width + x * 2 + dx) * 4;
                        b += bgra[p]; g += bgra[p + 1]; r += bgra[p + 2];
                    }
                }
                r >>= 2; g >>= 2; b >>= 2;
                int uv = ySize + y * width + x * 2;
                nv12[uv] = (byte)Math.Clamp(((-26 * r - 87 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                nv12[uv + 1] = (byte)Math.Clamp(((112 * r - 102 * g - 10 * b + 128) >> 8) + 128, 0, 255);
            }
        }
    }
}
