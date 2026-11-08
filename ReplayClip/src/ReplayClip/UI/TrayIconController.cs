using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ReplayClip.UI;

/// <summary>
/// Icono de la bandeja con su menú contextual. Usa NotifyIcon de WinForms (incluido en el Windows Desktop
/// Runtime): WPF no trae icono de bandeja propio y así evitamos una dependencia externa.
/// Los iconos se dibujan en tiempo de ejecución para no necesitar archivos .ico.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _saveItem;
    private readonly ToolStripMenuItem _recordItem;
    private readonly Icon _idleIcon;
    private readonly Icon _recordingIcon;

    public TrayIconController()
    {
        _idleIcon = CreateIcon(Color.FromArgb(0, 120, 215), recording: false);
        _recordingIcon = CreateIcon(Color.FromArgb(220, 40, 40), recording: true);

        _saveItem = new ToolStripMenuItem("Guardar repetición", null, (_, _) => SaveReplayRequested?.Invoke());
        _recordItem = new ToolStripMenuItem("Iniciar grabación", null, (_, _) => ToggleRecordingRequested?.Invoke());
        var menu = new ContextMenuStrip();
        menu.Items.Add(_saveItem);
        menu.Items.Add(_recordItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Abrir carpeta de clips", null, (_, _) => OpenFolderRequested?.Invoke()));
        menu.Items.Add(new ToolStripMenuItem("Ajustes…", null, (_, _) => SettingsRequested?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Salir", null, (_, _) => ExitRequested?.Invoke()));

        _notifyIcon = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "ReplayClip",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => SettingsRequested?.Invoke();
        Notifier = new ClipNotifier(_notifyIcon);
    }

    public event Action? SaveReplayRequested;
    public event Action? ToggleRecordingRequested;
    public event Action? OpenFolderRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public ClipNotifier Notifier { get; }

    /// <summary>Actualiza icono, menú y texto emergente.</summary>
    public void UpdateState(bool recording, string saveHotkey, string recordHotkey, int replaySeconds)
    {
        _notifyIcon.Icon = recording ? _recordingIcon : _idleIcon;
        _saveItem.Text = $"Guardar últimos {replaySeconds} s\t{saveHotkey}";
        _recordItem.Text = (recording ? "Detener grabación" : "Iniciar grabación") + $"\t{recordHotkey}";
        string text = recording ? "ReplayClip — grabando" : $"ReplayClip — repetición de {replaySeconds} s activa";
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        DestroyIcon(_idleIcon);
        DestroyIcon(_recordingIcon);
    }

    private static Icon CreateIcon(Color color, bool recording)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var ring = new Pen(color, 4);
            g.DrawEllipse(ring, 3, 3, 26, 26);
            using var fill = new SolidBrush(recording ? color : Color.White);
            g.FillEllipse(fill, 10, 10, 12, 12);
            if (!recording)
            {
                using var dot = new SolidBrush(color);
                g.FillEllipse(dot, 12, 12, 8, 8);
            }
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static void DestroyIcon(Icon icon)
    {
        DestroyIconNative(icon.Handle);
        icon.Dispose();
    }

    [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    private static extern bool DestroyIconNative(IntPtr handle);
}
