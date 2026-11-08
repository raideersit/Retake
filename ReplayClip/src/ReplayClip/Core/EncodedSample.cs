namespace ReplayClip.Core;

/// <summary>Tipo de pista dentro del MP4. El orden del enum es el orden de las pistas de audio.</summary>
public enum StreamKind
{
    Video = 0,
    /// <summary>Pista 1 de audio: mezcla sistema + micrófono (la que oyen todos los reproductores).</summary>
    AudioMix = 1,
    /// <summary>Pista 2 de audio: solo el sonido del sistema (WASAPI loopback).</summary>
    AudioSystem = 2,
    /// <summary>Pista 3 de audio: solo el micrófono.</summary>
    AudioMic = 3,
}

/// <summary>
/// Metadatos de una muestra ya codificada (H.264 o AAC). Los bytes viajan aparte,
/// como <see cref="ReadOnlySpan{T}"/>, para no reservar memoria por cada muestra.
/// </summary>
/// <param name="Kind">Pista a la que pertenece.</param>
/// <param name="Pts">Marca de presentación en 100 ns del dominio QPC (ver <see cref="MediaClock"/>).</param>
/// <param name="Duration">Duración en 100 ns.</param>
/// <param name="IsKeyFrame">Verdadero si es un fotograma clave (IDR); en audio, siempre verdadero.</param>
/// <param name="Generation">Generación de formato: cambia cuando se reinicia el codificador.</param>
public readonly record struct EncodedSampleInfo(
    StreamKind Kind,
    long Pts,
    long Duration,
    bool IsKeyFrame,
    int Generation);

/// <summary>Receptor de muestras codificadas (búfer circular, grabación a disco...).</summary>
public interface IEncodedSampleSink
{
    /// <summary>
    /// Recibe una muestra. Se llama desde hilos de codificación: la implementación debe ser
    /// rápida y copiar los bytes si los necesita después de retornar.
    /// </summary>
    void OnSample(in EncodedSampleInfo info, ReadOnlySpan<byte> data);

    /// <summary>Se publica un formato nuevo (antes de la primera muestra de esa generación).</summary>
    void OnFormat(StreamFormat format);
}
