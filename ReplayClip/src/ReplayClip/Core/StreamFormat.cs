namespace ReplayClip.Core;

/// <summary>
/// Descriptor inmutable del formato de una pista codificada. Guarda el tipo de medio de
/// Media Foundation serializado como blob (MFGetAttributesAsBlob), de modo que no depende de
/// la vida de ningún objeto COM y el muxer puede reconstruirlo cuando quiera.
/// </summary>
public sealed class StreamFormat
{
    public StreamFormat(StreamKind kind, int generation, byte[] mediaTypeBlob, string description)
    {
        Kind = kind;
        Generation = generation;
        MediaTypeBlob = mediaTypeBlob;
        Description = description;
    }

    public StreamKind Kind { get; }

    /// <summary>Generación del codificador que produjo este formato.</summary>
    public int Generation { get; }

    /// <summary>Atributos del IMFMediaType de salida del codificador (incluye SPS/PPS o AudioSpecificConfig).</summary>
    public byte[] MediaTypeBlob { get; }

    /// <summary>Texto legible para el registro, p. ej. "H.264 1920x1080 60 fps".</summary>
    public string Description { get; }

    public override string ToString() => $"{Kind} gen {Generation}: {Description}";
}
