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
        _idleIcon = CreatePixelIcon(recording: false);
        _recordingIcon = CreatePixelIcon(recording: true);

        _saveItem = new ToolStripMenuItem("Guardar repetición", null, (_, _) => SaveReplayRequested?.Invoke());
        _recordItem = new ToolStripMenuItem("Iniciar grabación", null, (_, _) => ToggleRecordingRequested?.Invoke());
        // Menú clásico estilo Windows 95: gris, relieve 3D, selección azul marino. Solo se dibuja al abrirlo.
        var menu = new ContextMenuStrip
        {
            Renderer = new RetroMenuRenderer(),
            Font = new Font("Microsoft Sans Serif", 8.25f),
            ShowImageMargin = false,
            Padding = new Padding(2),
        };
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

    /// <summary>Menú contextual (para la vista previa de la herramienta de pruebas).</summary>
    internal ContextMenuStrip Menu => _notifyIcon.ContextMenuStrip!;

    /// <summary>Actualiza icono, menú y texto emergente.</summary>
    public void UpdateState(bool recording, string saveHotkey, string recordHotkey, int replaySeconds)
    {
        _notifyIcon.Icon = recording ? _recordingIcon : _idleIcon;
        // El atajo va en su propia columna a la derecha, como en los menús clásicos.
        _saveItem.Text = $"Guardar últimos {replaySeconds} s";
        _saveItem.ShortcutKeyDisplayString = saveHotkey;
        _recordItem.Text = recording ? "Detener grabación" : "Iniciar grabación";
        _recordItem.ShortcutKeyDisplayString = recordHotkey;
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

    /// <summary>
    /// Icono pixel-art de 16×16 (dibujado a 32×32 con píxeles dobles): un monitor clásico con la pantalla
    /// azul marino y un punto que es azul en reposo y rojo mientras graba.
    /// </summary>
    internal static Icon CreatePixelIcon(bool recording)
    {
        var black = Color.Black;
        var face = Color.FromArgb(0xC0, 0xC0, 0xC0);
        var white = Color.White;
        var shadow = Color.FromArgb(0x80, 0x80, 0x80);
        var navy = Color.FromArgb(0x00, 0x00, 0x80);
        var dot = recording ? Color.FromArgb(0xFF, 0x00, 0x00) : Color.FromArgb(0x10, 0x84, 0xD0);

        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.None;
            g.Clear(Color.Transparent);
            void Px(Color color, int x, int y, int w = 1, int h = 1)
            {
                using var brush = new SolidBrush(color);
                g.FillRectangle(brush, x * 2, y * 2, w * 2, h * 2);
            }

            Px(black, 0, 1, 16, 12);   // contorno del monitor
            Px(face, 1, 2, 14, 10);    // carcasa
            Px(white, 1, 2, 14, 1);    // relieve superior
            Px(white, 1, 2, 1, 10);    // relieve izquierdo
            Px(shadow, 2, 11, 13, 1);  // sombra inferior
            Px(shadow, 14, 3, 1, 9);   // sombra derecha
            Px(shadow, 3, 3, 10, 1);   // marco hundido de la pantalla
            Px(navy, 3, 4, 10, 6);     // pantalla
            Px(dot, 9, 5, 3, 3);       // punto de estado
            if (recording) Px(white, 9, 5); // brillo del punto rojo
            Px(black, 5, 13, 6, 1);    // pie
            Px(black, 3, 14, 10, 2);   // base
            Px(face, 4, 14, 8, 1);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    /// <summary>Colores del menú clásico: gris, sin degradados, selección azul marino.</summary>
    private sealed class RetroMenuColors : ProfessionalColorTable
    {
        private static readonly Color Face = Color.FromArgb(0xC0, 0xC0, 0xC0);
        private static readonly Color Navy = Color.FromArgb(0x00, 0x00, 0x80);

        public override Color ToolStripDropDownBackground => Face;
        public override Color ImageMarginGradientBegin => Face;
        public override Color ImageMarginGradientMiddle => Face;
        public override Color ImageMarginGradientEnd => Face;
        public override Color MenuItemSelected => Navy;
        public override Color MenuItemSelectedGradientBegin => Navy;
        public override Color MenuItemSelectedGradientEnd => Navy;
        public override Color MenuItemBorder => Navy;
        public override Color MenuBorder => Color.Black;
        public override Color SeparatorDark => Color.FromArgb(0x80, 0x80, 0x80);
        public override Color SeparatorLight => Color.White;
    }

    /// <summary>Dibuja el menú como en Windows 95: borde en relieve y texto blanco sobre la selección.</summary>
    private sealed class RetroMenuRenderer : ToolStripProfessionalRenderer
    {
        public RetroMenuRenderer() : base(new RetroMenuColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Color.White : Color.Black;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            ControlPaint.DrawBorder3D(e.Graphics, new Rectangle(Point.Empty, e.ToolStrip.Size), Border3DStyle.Raised);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var dark = new Pen(Color.FromArgb(0x80, 0x80, 0x80));
            e.Graphics.DrawLine(dark, 2, y, e.Item.Width - 3, y);
            e.Graphics.DrawLine(Pens.White, 2, y + 1, e.Item.Width - 3, y + 1);
        }
    }

    private static void DestroyIcon(Icon icon)
    {
        DestroyIconNative(icon.Handle);
        icon.Dispose();
    }

    [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    private static extern bool DestroyIconNative(IntPtr handle);
}
