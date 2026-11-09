using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using ReplayClip.Editing;

namespace ReplayClip.UI;

/// <summary>Base mínima para notificar cambios a la interfaz.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>Un tramo del montaje tal como lo muestra y edita la ventana.</summary>
public sealed class SegmentItem : ObservableObject
{
    private int _number;

    public SegmentItem(EditSegment model)
    {
        Model = model;
    }

    public EditSegment Model { get; }

    public string FileName => Path.GetFileName(Model.Path);

    public int Number
    {
        get => _number;
        set { if (Set(ref _number, value)) OnPropertyChanged(nameof(Title)); }
    }

    public string Title => $"{Number}. {FileName}";

    public double InSeconds
    {
        get => Model.InSeconds;
        set
        {
            double clamped = Math.Clamp(value, 0, Math.Max(0, Model.OutSeconds - EditTimeline.MinSegmentSeconds));
            if (Math.Abs(clamped - Model.InSeconds) < 1e-6) return;
            Model.InSeconds = clamped;
            RaiseRange();
        }
    }

    public double OutSeconds
    {
        get => Model.OutSeconds;
        set
        {
            double clamped = Math.Clamp(value, Model.InSeconds + EditTimeline.MinSegmentSeconds, Model.SourceDuration);
            if (Math.Abs(clamped - Model.OutSeconds) < 1e-6) return;
            Model.OutSeconds = clamped;
            RaiseRange();
        }
    }

    /// <summary>Inicio como texto editable (m:ss.f).</summary>
    public string InText
    {
        get => TimeText.Format(InSeconds);
        set { if (TimeText.TryParse(value, out double s)) InSeconds = s; OnPropertyChanged(); }
    }

    /// <summary>Fin como texto editable (m:ss.f).</summary>
    public string OutText
    {
        get => TimeText.Format(OutSeconds);
        set { if (TimeText.TryParse(value, out double s)) OutSeconds = s; OnPropertyChanged(); }
    }

    public TransitionKind Transition
    {
        get => Model.Transition;
        set
        {
            if (Model.Transition == value) return;
            Model.Transition = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(HasTransitionDuration));
        }
    }

    public double TransitionSeconds
    {
        get => Model.TransitionSeconds;
        set
        {
            double rounded = Math.Round(Math.Clamp(value, 0.1, 3), 1);
            if (Math.Abs(Model.TransitionSeconds - rounded) < 1e-6) return;
            Model.TransitionSeconds = rounded;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Summary));
        }
    }

    public bool HasTransitionDuration => Transition != TransitionKind.Cut;

    /// <summary>Línea de resumen: rango, duración y transición al siguiente.</summary>
    public string Summary =>
        $"{TimeText.Format(InSeconds)} → {TimeText.Format(OutSeconds)} ({Model.Length.ToString("0.0", CultureInfo.CurrentCulture)} s)" +
        (Transition == TransitionKind.Cut ? "" : $" · {EditorViewModel.TransitionLabel(Transition)} {TransitionSeconds.ToString("0.0", CultureInfo.CurrentCulture)} s");

    private void RaiseRange()
    {
        OnPropertyChanged(nameof(InSeconds));
        OnPropertyChanged(nameof(OutSeconds));
        OnPropertyChanged(nameof(InText));
        OnPropertyChanged(nameof(OutText));
        OnPropertyChanged(nameof(Summary));
    }
}

/// <summary>Estado de la ventana del editor de clips.</summary>
public sealed class EditorViewModel : ObservableObject
{
    private SegmentItem? _selected;
    private bool _isExporting;
    private double _progress;
    private string _status = "Agrega clips para empezar. Puedes arrastrarlos desde el Explorador.";
    private string _totalText = "";

    public EditorViewModel()
    {
        Segments.CollectionChanged += (_, _) => Renumber();
    }

    public ObservableCollection<SegmentItem> Segments { get; } = new();

    public IReadOnlyList<Choice<TransitionKind>> TransitionChoices { get; } = Enum.GetValues<TransitionKind>()
        .Select(k => new Choice<TransitionKind>(k, TransitionLabel(k))).ToList();

    public SegmentItem? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(NoSelection));
        }
    }

    public bool HasSelection => _selected is not null;

    public bool NoSelection => _selected is null;

    public bool IsExporting
    {
        get => _isExporting;
        set { if (Set(ref _isExporting, value)) OnPropertyChanged(nameof(IsIdle)); }
    }

    public bool IsIdle => !_isExporting;

    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public string TotalText
    {
        get => _totalText;
        private set => Set(ref _totalText, value);
    }

    public static string TransitionLabel(TransitionKind kind) => kind switch
    {
        TransitionKind.Cut => "Corte (sin transición)",
        TransitionKind.Dissolve => "Disolver",
        TransitionKind.FadeThroughBlack => "Fundido a negro",
        TransitionKind.Wipe => "Barrido",
        _ => kind.ToString(),
    };

    public SegmentItem Add(EditSegment segment)
    {
        var item = new SegmentItem(segment);
        item.PropertyChanged += (_, _) => UpdateTotal();
        Segments.Add(item);
        return item;
    }

    /// <summary>Parte el tramo seleccionado en el instante <paramref name="sourceSeconds"/> del archivo.</summary>
    public bool SplitSelected(double sourceSeconds)
    {
        if (_selected is null) return false;
        var model = _selected.Model;
        if (sourceSeconds <= model.InSeconds + EditTimeline.MinSegmentSeconds || sourceSeconds >= model.OutSeconds - EditTimeline.MinSegmentSeconds)
        {
            return false;
        }
        var second = model.Clone();
        second.InSeconds = sourceSeconds;
        // La primera mitad pasa a la segunda con un corte; la segunda conserva la transición original.
        var first = _selected;
        first.OutSeconds = sourceSeconds;
        first.Transition = TransitionKind.Cut;
        var item = new SegmentItem(second);
        item.PropertyChanged += (_, _) => UpdateTotal();
        Segments.Insert(Segments.IndexOf(first) + 1, item);
        Selected = item;
        return true;
    }

    public void RemoveSelected()
    {
        if (_selected is null) return;
        int index = Segments.IndexOf(_selected);
        Segments.RemoveAt(index);
        Selected = Segments.Count == 0 ? null : Segments[Math.Min(index, Segments.Count - 1)];
    }

    public void MoveSelected(int delta)
    {
        if (_selected is null) return;
        int index = Segments.IndexOf(_selected);
        int target = index + delta;
        if (target < 0 || target >= Segments.Count) return;
        var item = _selected;
        Segments.Move(index, target);
        Selected = item;
    }

    public IReadOnlyList<EditSegment> Models() => Segments.Select(s => s.Model).ToList();

    private void Renumber()
    {
        for (int i = 0; i < Segments.Count; i++) Segments[i].Number = i + 1;
        UpdateTotal();
    }

    private void UpdateTotal()
    {
        var timeline = new EditTimeline(Models());
        TotalText = Segments.Count == 0
            ? "Montaje vacío"
            : $"{Segments.Count} tramo(s) · duración final {TimeText.Format(timeline.Duration)}";
    }
}
