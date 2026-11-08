using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using ReplayClip.Core;
using ReplayClip.Settings;

namespace ReplayClip.UI;

/// <summary>Ventana de ajustes. Solo existe mientras está abierta: cerrada no consume memoria de UI.</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _model;
    private readonly RecorderEngine _engine;
    private readonly DispatcherTimer _statusTimer;

    public SettingsWindow(AppSettings settings, RecorderEngine engine)
    {
        InitializeComponent();
        _engine = engine;
        _model = new SettingsViewModel(settings);
        DataContext = _model;
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStatus(), Dispatcher);
        _statusTimer.Start();
        UpdateStatus();
        Closed += (_, _) => _statusTimer.Stop();
    }

    /// <summary>Ajustes aceptados por el usuario (null si canceló).</summary>
    public AppSettings? Result { get; private set; }

    private void UpdateStatus()
    {
        double mb = _engine.BufferBytes / (1024.0 * 1024.0);
        using var process = Process.GetCurrentProcess();
        double ram = process.PrivateMemorySize64 / (1024.0 * 1024.0);
        _model.Status = $"{_engine.VideoDescription}\n{_engine.CaptureDescription}\n" +
                        $"Búfer: {_engine.BufferedSeconds:F0} s en {mb:F0} MB · Memoria privada del proceso: {ram:F0} MB" +
                        (_engine.IsRecording ? " · GRABANDO" : "");
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _model.RefreshSources();

    // Barra de título propia (estilo Windows 95): arrastrar, minimizar y cerrar.
    private void OnTitleBarMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Carpeta para clips y grabaciones",
            InitialDirectory = Directory.Exists(_model.OutputFolder) ? _model.OutputFolder : AppSettings.DefaultOutputFolder,
        };
        if (dialog.ShowDialog(this) == true)
        {
            _model.OutputFolder = dialog.FolderName;
            DataContext = null;
            DataContext = _model;
        }
    }

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Logger.CurrentLogPath))
        {
            Process.Start(new ProcessStartInfo(Logger.CurrentLogPath) { UseShellExecute = true });
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        string? error = _model.Validate();
        if (error is not null)
        {
            MessageBox.Show(this, error, "ReplayClip", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = _model.ToSettings();
        Close();
    }
}
