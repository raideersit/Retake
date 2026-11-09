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
        _idleIcon = CreateLogoIcon(recording: false);
        _recordingIcon = CreateLogoIcon(recording: true);

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
        menu.Items.Add(new ToolStripMenuItem("Editor de clips…", null, (_, _) => EditorRequested?.Invoke()));
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
    public event Action? EditorRequested;
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

    /// <summary>Ruta del logo dentro del ensamblado (Assets\logo-zorro.png, acción de compilación Resource).</summary>
    private static readonly Uri LogoUri = new("pack://application:,,,/ReplayClip;component/Assets/logo-zorro.png");

    /// <summary>
    /// Icono de la bandeja: el logo del zorro escalado sin suavizado (píxeles nítidos) al tamaño de icono
    /// pequeño del sistema (16 px al 100 %, 20 px al 125 %...). Mientras graba lleva un punto rojo de "REC"
    /// en la esquina superior derecha. Se genera dos veces al arrancar y nunca más.
    /// </summary>
    internal static Icon CreateLogoIcon(bool recording)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var stream = System.Windows.Application.GetResourceStream(LogoUri).Stream;
        using var logo = Image.FromStream(stream);
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.None;
            g.DrawImage(logo, 0, 0, size, size);
            if (recording) DrawRecordDot(g, size);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    /// <summary>
    /// Punto rojo de "REC" en pixel-art (un círculo de 7×7 con borde blanco, fila a fila) en la esquina
    /// superior derecha. Un FillEllipse tan pequeño y sin suavizado parecería una cruz.
    /// </summary>
    private static void DrawRecordDot(Graphics g, int size)
    {
        // Anchura de cada fila del círculo exterior (blanco) e interior (rojo), centradas.
        int[] outer = { 3, 5, 7, 7, 7, 5, 3 };
        int[] inner = { 0, 3, 5, 5, 5, 3, 0 };
        int scale = Math.Max(1, size / 16);
        int left = size - 7 * scale;
        using var red = new SolidBrush(Color.FromArgb(0xFF, 0x20, 0x20));
        for (int row = 0; row < outer.Length; row++)
        {
            int y = row * scale;
            g.FillRectangle(Brushes.White, left + (7 - outer[row]) / 2 * scale, y, outer[row] * scale, scale);
            if (inner[row] > 0) g.FillRectangle(red, left + (7 - inner[row]) / 2 * scale, y, inner[row] * scale, scale);
        }
    }

    /// <summary>Colores del menú clásico desde <see cref="RetroPalette"/>: cara lavanda, sin degradados, selección índigo.</summary>
    private sealed class RetroMenuColors : ProfessionalColorTable
    {
        internal static readonly Color Face = RetroPalette.ToDrawing(RetroPalette.Surface);
        internal static readonly Color Primary = RetroPalette.ToDrawing(RetroPalette.Primary);
        internal static readonly Color OnPrimary = RetroPalette.ToDrawing(RetroPalette.OnPrimary);
        internal static readonly Color Text = RetroPalette.ToDrawing(RetroPalette.Text);
        internal static readonly Color Shadow = RetroPalette.ToDrawing(RetroPalette.BevelShadow);
        internal static readonly Color Highlight = RetroPalette.ToDrawing(RetroPalette.BevelHighlight);

        public override Color ToolStripDropDownBackground => Face;
        public override Color ImageMarginGradientBegin => Face;
        public override Color ImageMarginGradientMiddle => Face;
        public override Color ImageMarginGradientEnd => Face;
        public override Color MenuItemSelected => Primary;
        public override Color MenuItemSelectedGradientBegin => Primary;
        public override Color MenuItemSelectedGradientEnd => Primary;
        public override Color MenuItemBorder => Primary;
        public override Color MenuBorder => Text;
        public override Color SeparatorDark => Shadow;
        public override Color SeparatorLight => Highlight;
    }

    /// <summary>Dibuja el menú como en Windows 95: borde en relieve y texto lavanda sobre la selección.</summary>
    private sealed class RetroMenuRenderer : ToolStripProfessionalRenderer
    {
        public RetroMenuRenderer() : base(new RetroMenuColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? RetroMenuColors.OnPrimary : RetroMenuColors.Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // Relieve clásico con los tonos de la paleta (DrawBorder3D usaría los grises del sistema).
            var g = e.Graphics;
            int w = e.ToolStrip.Width - 1, h = e.ToolStrip.Height - 1;
            using var light = new Pen(RetroMenuColors.Highlight);
            using var shadow = new Pen(RetroMenuColors.Shadow);
            using var dark = new Pen(RetroMenuColors.Text);
            g.DrawLine(light, 0, 0, w, 0);
            g.DrawLine(light, 0, 0, 0, h);
            g.DrawLine(dark, 0, h, w, h);
            g.DrawLine(dark, w, 0, w, h);
            g.DrawLine(shadow, 1, h - 1, w - 1, h - 1);
            g.DrawLine(shadow, w - 1, 1, w - 1, h - 1);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var dark = new Pen(RetroMenuColors.Shadow);
            using var light = new Pen(RetroMenuColors.Highlight);
            e.Graphics.DrawLine(dark, 2, y, e.Item.Width - 3, y);
            e.Graphics.DrawLine(light, 2, y + 1, e.Item.Width - 3, y + 1);
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
