namespace ReplayClip.Core;

/// <summary>
/// Reparte cada muestra codificada a todos los receptores (búfer de repetición y, si está activa,
/// la grabación continua). Un único codificador alimenta ambos destinos: grabar no cuesta GPU extra.
/// Mantiene además el último formato conocido de cada pista para quien se suscriba tarde.
/// </summary>
public sealed class EncodedSampleBus
{
    private readonly object _gate = new();
    private IEncodedSampleSink[] _sinks = Array.Empty<IEncodedSampleSink>();
    private readonly Dictionary<StreamKind, StreamFormat> _latestFormats = new();

    /// <summary>Suscribe un receptor y le reenvía los formatos vigentes.</summary>
    public void Subscribe(IEncodedSampleSink sink)
    {
        lock (_gate)
        {
            foreach (var format in _latestFormats.Values) sink.OnFormat(format);
            _sinks = _sinks.Append(sink).ToArray();
        }
    }

    public void Unsubscribe(IEncodedSampleSink sink)
    {
        lock (_gate) _sinks = _sinks.Where(s => !ReferenceEquals(s, sink)).ToArray();
    }

    /// <summary>Último formato publicado de una pista, o null si aún no existe.</summary>
    public StreamFormat? GetLatestFormat(StreamKind kind)
    {
        lock (_gate) return _latestFormats.GetValueOrDefault(kind);
    }

    /// <summary>Olvida el formato de una pista (p. ej. cuando el micrófono se desactiva).</summary>
    public void ClearFormat(StreamKind kind)
    {
        lock (_gate) _latestFormats.Remove(kind);
    }

    public void PublishFormat(StreamFormat format)
    {
        IEncodedSampleSink[] sinks;
        lock (_gate)
        {
            _latestFormats[format.Kind] = format;
            sinks = _sinks;
        }
        foreach (var sink in sinks) sink.OnFormat(format);
    }

    public void PublishSample(in EncodedSampleInfo info, ReadOnlySpan<byte> data)
    {
        // Lectura sin bloqueo: el arreglo se reemplaza entero al suscribir (copy-on-write).
        var sinks = Volatile.Read(ref _sinks);
        foreach (var sink in sinks)
        {
            try { sink.OnSample(info, data); }
            catch (Exception ex) { Logger.Error($"Receptor {sink.GetType().Name} falló con una muestra", ex); }
        }
    }
}
