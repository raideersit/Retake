namespace ReplayClip.Audio;

/// <summary>Amplifica muestras PCM de 16 bits (con saturación para no desbordar).</summary>
public static class AudioGain
{
    /// <summary>
    /// Devuelve <paramref name="samples"/> multiplicadas por <paramref name="gain"/>. Con ganancia 1 devuelve
    /// la misma memoria sin copiar; si no, usa <paramref name="buffer"/> (que se amplía si hace falta).
    /// </summary>
    public static ReadOnlySpan<short> Apply(ReadOnlySpan<short> samples, float gain, ref short[] buffer)
    {
        if (gain is > 0.999f and < 1.001f) return samples;
        if (buffer.Length < samples.Length) buffer = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            buffer[i] = (short)Math.Clamp((int)MathF.Round(samples[i] * gain), short.MinValue, short.MaxValue);
        }
        return buffer.AsSpan(0, samples.Length);
    }
}
