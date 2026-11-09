using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ReplayClip.Core;
using ReplayClip.Editing;
using SharpGen.Runtime;

namespace ReplayClip.UI;

/// <summary>
/// Editor de clips: une varios clips en un montaje, recorta cada tramo, lo divide y elige la transición entre
/// tramos; luego lo exporta a un MP4 nuevo con <see cref="EditExporter"/>. La vista previa usa MediaElement
/// (el reproductor de Windows) y muestra el archivo original; las transiciones solo aparecen en el video exportado.
/// Como la ventana de ajustes, solo existe mientras está abierta.
/// </summary>
public partial class EditorWindow : Window
{
    private readonly EditorViewModel _model = new();
    private readonly Func<(string Folder, int BitrateMbps)> _outputSettings;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _exportCancellation;
    private string? _loadedPath;
    private bool _playing;
    private bool _updatingSlider;
    private double? _stopAt;          // "Ver tramo": pausa al llegar al fin marcado
    private string? _lastExport;

    /// <param name="outputSettings">Carpeta de salida y bitrate actuales (se leen al exportar).</param>
    public EditorWindow(Func<(string Folder, int BitrateMbps)> outputSettings)
    {
        InitializeComponent();
        _outputSettings = outputSettings;
        DataContext = _model;
        _model.PropertyChanged += OnModelPropertyChanged;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => OnTick(), Dispatcher);
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _timer.Stop();
            Player.Close();
        };
    }

    /// <summary>Añade archivos al final del montaje (desde el diálogo, al arrastrar o al abrir con un clip).</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        SegmentItem? last = null;
        var failed = new List<string>();
        foreach (string path in paths)
        {
            try
            {
                var info = ClipReader.Probe(path);
                if (info.DurationSeconds < EditTimeline.MinSegmentSeconds || info.Width <= 0)
                {
                    failed.Add(Path.GetFileName(path));
                    continue;
                }
                last = _model.Add(new EditSegment(path, info.DurationSeconds));
            }
            catch (Exception ex) when (ex is SharpGenException or IOException or UnauthorizedAccessException or InvalidCastException)
            {
                Logger.Warn($"Editor: no se pudo abrir {path} ({ex.Message}).");
                failed.Add(Path.GetFileName(path));
            }
        }
        if (last is not null) _model.Selected = last;
        _model.Status = failed.Count == 0
            ? "Selecciona un tramo para recortarlo. El orden de la lista es el orden del video final."
            : $"No se pudieron abrir: {string.Join(", ", failed)} (¿no es un video o está dañado?).";
    }

    // ───── Montaje ─────

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var (folder, _) = _outputSettings();
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Agregar clips al montaje",
            Filter = "Videos MP4 (*.mp4)|*.mp4|Todos los videos (*.mp4;*.mov;*.m4v;*.wmv)|*.mp4;*.mov;*.m4v;*.wmv|Todos los archivos|*.*",
            Multiselect = true,
            InitialDirectory = Directory.Exists(folder) ? folder : "",
        };
        if (dialog.ShowDialog(this) == true) AddFiles(dialog.FileNames.OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase));
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!_model.IsIdle || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        AddFiles(files.Where(File.Exists).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase));
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e) => _model.RemoveSelected();

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => _model.MoveSelected(-1);

    private void OnMoveDownClick(object sender, RoutedEventArgs e) => _model.MoveSelected(+1);

    // ───── Vista previa ─────

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.Selected)) LoadSelected();
    }

    private void LoadSelected()
    {
        Pause();
        _stopAt = null;
        var item = _model.Selected;
        if (item is null)
        {
            Player.Close();
            Player.Source = null;
            _loadedPath = null;
            UpdatePositionText(0, 0);
            return;
        }
        if (!string.Equals(_loadedPath, item.Model.Path, StringComparison.OrdinalIgnoreCase))
        {
            _loadedPath = item.Model.Path;
            Player.Source = new Uri(item.Model.Path);
            // Pausado muestra el fotograma de la posición (ScrubbingEnabled) sin reproducir.
            Player.Pause();
        }
        _updatingSlider = true;
        PositionSlider.Maximum = Math.Max(0.1, item.Model.SourceDuration);
        _updatingSlider = false;
        Seek(item.InSeconds);
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (_model.Selected is { } item && Player.NaturalDuration.HasTimeSpan)
        {
            _updatingSlider = true;
            PositionSlider.Maximum = Math.Max(0.1, Player.NaturalDuration.TimeSpan.TotalSeconds);
            _updatingSlider = false;
            Seek(item.InSeconds);
        }
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        _model.Status = $"La vista previa no puede reproducir este archivo ({e.ErrorException.Message}). Aun así se puede exportar.";
        Logger.Warn($"Editor: vista previa fallida: {e.ErrorException.Message}");
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e) => Pause();

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_playing)
        {
            Pause();
            return;
        }
        _stopAt = null;
        Play();
    }

    private void OnPlaySegmentClick(object sender, RoutedEventArgs e)
    {
        if (_model.Selected is not { } item) return;
        Seek(item.InSeconds);
        _stopAt = item.OutSeconds;
        Play();
    }

    private void Play()
    {
        if (_model.Selected is null) return;
        Player.Play();
        _playing = true;
        PlayButton.Content = "Pausa";
        _timer.Start();
    }

    private void Pause()
    {
        if (_loadedPath is not null) Player.Pause();
        _playing = false;
        PlayButton.Content = "Reproducir";
        _timer.Stop();
        OnTick();
    }

    private void Seek(double seconds)
    {
        Player.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
        SetSlider(seconds);
    }

    private double CurrentPosition => Player.Position.TotalSeconds;

    private void OnTick()
    {
        double position = CurrentPosition;
        if (_stopAt is double stop && position >= stop)
        {
            _stopAt = null;
            Pause();
            Seek(stop);
            return;
        }
        SetSlider(position);
    }

    private void SetSlider(double seconds)
    {
        _updatingSlider = true;
        PositionSlider.Value = Math.Clamp(seconds, 0, PositionSlider.Maximum);
        _updatingSlider = false;
        UpdatePositionText(seconds, PositionSlider.Maximum);
    }

    private void UpdatePositionText(double position, double duration) =>
        PositionText.Text = $"{TimeText.Format(position)} / {TimeText.Format(duration)}";

    private void OnPositionSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider || _model.Selected is null) return;
        Player.Position = TimeSpan.FromSeconds(e.NewValue);
        UpdatePositionText(e.NewValue, PositionSlider.Maximum);
    }

    private void OnMarkInClick(object sender, RoutedEventArgs e)
    {
        if (_model.Selected is not { } item) return;
        double position = CurrentPosition;
        if (position >= item.OutSeconds - EditTimeline.MinSegmentSeconds)
        {
            _model.Status = "El inicio tiene que quedar antes del fin. Mueve la posición o cambia primero el fin.";
            return;
        }
        item.InSeconds = position;
    }

    private void OnMarkOutClick(object sender, RoutedEventArgs e)
    {
        if (_model.Selected is not { } item) return;
        double position = CurrentPosition;
        if (position <= item.InSeconds + EditTimeline.MinSegmentSeconds)
        {
            _model.Status = "El fin tiene que quedar después del inicio. Mueve la posición o cambia primero el inicio.";
            return;
        }
        item.OutSeconds = position;
    }

    private void OnSplitClick(object sender, RoutedEventArgs e)
    {
        double position = CurrentPosition;
        _model.Status = _model.SplitSelected(position)
            ? $"Tramo dividido en {TimeText.Format(position)}. Ahora puedes quitar, mover o recortar cada parte."
            : "Para dividir, pon la posición dentro del tramo (entre su inicio y su fin).";
    }

    /// <summary>Enter en una casilla de tiempo aplica el valor sin tener que salir de ella.</summary>
    private void OnTimeBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (_model.Selected is { } item) Seek(item.InSeconds);
        e.Handled = true;
    }

    // ───── Exportar ─────

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var segments = _model.Models();
        var timeline = new EditTimeline(segments);
        if (timeline.Segments.Count == 0)
        {
            _model.Status = "Agrega al menos un clip al montaje antes de exportar.";
            return;
        }
        Pause();
        var (folder, bitrate) = _outputSettings();
        _exportCancellation = new CancellationTokenSource();
        _model.IsExporting = true;
        _model.Progress = 0;
        _model.Status = $"Exportando {TimeText.Format(timeline.Duration)} de video… (puedes seguir jugando; va con prioridad baja)";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var progress = new Progress<double>(p => _model.Progress = p);
            _lastExport = await EditExporter.ExportAsync(segments, folder, bitrate, progress, _exportCancellation.Token);
            _model.Status = $"Listo en {stopwatch.Elapsed.TotalSeconds:F0} s: {_lastExport}";
        }
        catch (OperationCanceledException)
        {
            _model.Progress = 0;
            _model.Status = "Exportación cancelada.";
        }
        catch (Exception ex)
        {
            Logger.Error("Editor: falló la exportación", ex);
            _model.Progress = 0;
            _model.Status = $"No se pudo exportar: {ex.Message}";
        }
        finally
        {
            _model.IsExporting = false;
            _exportCancellation.Dispose();
            _exportCancellation = null;
        }
    }

    private void OnCancelExportClick(object sender, RoutedEventArgs e) => _exportCancellation?.Cancel();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (_lastExport is not null && File.Exists(_lastExport))
        {
            // Abre la carpeta con el video recién exportado seleccionado.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastExport}\"") { UseShellExecute = true });
            return;
        }
        var (folder, _) = _outputSettings();
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_model.IsExporting) return;
        var answer = MessageBox.Show(this, "Hay una exportación en curso. ¿Cancelarla y cerrar el editor?", "ReplayClip",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) _exportCancellation?.Cancel();
        else e.Cancel = true;
    }

    // ───── Barra de título ─────

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
