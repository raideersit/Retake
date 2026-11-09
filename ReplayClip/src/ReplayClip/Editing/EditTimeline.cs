namespace ReplayClip.Editing;

/// <summary>Transición entre un tramo y el siguiente.</summary>
public enum TransitionKind
{
    /// <summary>Corte seco, sin solape.</summary>
    Cut,
    /// <summary>Disolver: la imagen y el audio de A se mezclan con los de B.</summary>
    Dissolve,
    /// <summary>Fundido a negro: A se apaga hasta negro y B se enciende desde negro.</summary>
    FadeThroughBlack,
    /// <summary>Barrido: B entra de izquierda a derecha sobre A (el audio se mezcla).</summary>
    Wipe,
}

/// <summary>
/// Un tramo del montaje: una parte [<see cref="InSeconds"/>, <see cref="OutSeconds"/>) de un archivo,
/// y la transición hacia el tramo siguiente.
/// </summary>
public sealed class EditSegment
{
    public EditSegment(string path, double sourceDuration)
    {
        Path = path;
        SourceDuration = Math.Max(0, sourceDuration);
        OutSeconds = SourceDuration;
    }

    public string Path { get; }

    /// <summary>Duración total del archivo de origen.</summary>
    public double SourceDuration { get; }

    public double InSeconds { get; set; }

    public double OutSeconds { get; set; }

    public TransitionKind Transition { get; set; } = TransitionKind.Cut;

    /// <summary>Duración de la transición hacia el tramo siguiente (se ignora con <see cref="TransitionKind.Cut"/>).</summary>
    public double TransitionSeconds { get; set; } = 0.5;

    public double Length => Math.Max(0, OutSeconds - InSeconds);

    public EditSegment Clone() => (EditSegment)MemberwiseClone();
}

/// <summary>Qué se ve y se oye en un instante del montaje: uno o dos tramos y cómo se combinan.</summary>
public readonly record struct Composition(
    int First, double FirstSourceTime,
    int Second, double SecondSourceTime,
    TransitionKind Kind, double Progress)
{
    public bool IsTransition => Second >= 0;

    /// <summary>Peso del primer tramo (imagen en disolver/fundido y volumen en todas).</summary>
    public double FirstWeight => IsTransition ? Weights(Kind, Progress).First : 1;

    /// <summary>Peso del segundo tramo.</summary>
    public double SecondWeight => IsTransition ? Weights(Kind, Progress).Second : 0;

    /// <summary>Pesos de los dos tramos en un punto <paramref name="progress"/> (0 a 1) de la transición.</summary>
    public static (double First, double Second) Weights(TransitionKind kind, double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        return kind == TransitionKind.FadeThroughBlack
            ? (Math.Max(0, 1 - 2 * progress), Math.Max(0, 2 * progress - 1))
            : (1 - progress, progress);
    }
}

/// <summary>
/// Coloca los tramos en la línea de tiempo de salida. Cada transición que no sea un corte solapa el final
/// de un tramo con el principio del siguiente durante su duración, así que nunca hay más de dos tramos a la
/// vez (la duración se limita a la mitad del tramo más corto de los dos). Lógica pura, sin Media Foundation.
/// </summary>
public sealed class EditTimeline
{
    /// <summary>Tramos más cortos que esto se ignoran (no aportan ni un fotograma).</summary>
    public const double MinSegmentSeconds = 0.1;

    private readonly EditSegment[] _segments;
    private readonly double[] _starts;
    private readonly double[] _overlaps;

    public EditTimeline(IEnumerable<EditSegment> segments)
    {
        _segments = segments.Where(s => s.Length >= MinSegmentSeconds).ToArray();
        _starts = new double[_segments.Length];
        _overlaps = new double[_segments.Length];
        double time = 0;
        for (int i = 0; i < _segments.Length; i++)
        {
            _starts[i] = time;
            _overlaps[i] = i + 1 < _segments.Length ? EffectiveTransition(_segments[i], _segments[i + 1]) : 0;
            time += _segments[i].Length - _overlaps[i];
        }
        Duration = _segments.Length == 0 ? 0 : _starts[^1] + _segments[^1].Length;
    }

    public IReadOnlyList<EditSegment> Segments => _segments;

    /// <summary>Duración total del montaje en segundos.</summary>
    public double Duration { get; }

    /// <summary>Instante de salida en que empieza el tramo <paramref name="index"/>.</summary>
    public double StartOf(int index) => _starts[index];

    /// <summary>Instante de salida en que termina el tramo <paramref name="index"/>.</summary>
    public double EndOf(int index) => _starts[index] + _segments[index].Length;

    /// <summary>Duración real del solape entre el tramo <paramref name="index"/> y el siguiente.</summary>
    public double OverlapAfter(int index) => _overlaps[index];

    /// <summary>Duración que tendrá de verdad la transición (0 si es corte; limitada a la mitad de cada tramo).</summary>
    public static double EffectiveTransition(EditSegment a, EditSegment b)
    {
        if (a.Transition == TransitionKind.Cut) return 0;
        double limit = Math.Min(a.Length, b.Length) / 2;
        return Math.Clamp(a.TransitionSeconds, 0, limit);
    }

    /// <summary>Composición en el instante <paramref name="time"/> de la salida.</summary>
    public Composition At(double time)
    {
        if (_segments.Length == 0) return new Composition(-1, 0, -1, 0, TransitionKind.Cut, 0);
        time = Math.Clamp(time, 0, Duration);

        // El último tramo que ya empezó.
        int index = Array.BinarySearch(_starts, time);
        if (index < 0) index = ~index - 1;
        index = Math.Clamp(index, 0, _segments.Length - 1);

        // Si estamos dentro del solape con el anterior, la pareja es (anterior, este).
        if (index > 0 && time < EndOf(index - 1) && _overlaps[index - 1] > 0)
        {
            var prev = _segments[index - 1];
            double progress = (time - _starts[index]) / _overlaps[index - 1];
            return new Composition(
                index - 1, prev.InSeconds + (time - _starts[index - 1]),
                index, _segments[index].InSeconds + (time - _starts[index]),
                prev.Transition, Math.Clamp(progress, 0, 1));
        }
        return new Composition(index, _segments[index].InSeconds + (time - _starts[index]), -1, 0, TransitionKind.Cut, 0);
    }
}
