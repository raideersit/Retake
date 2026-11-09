using System.Windows.Media;

namespace ReplayClip.UI;

/// <summary>
/// Paleta única de la aplicación: Dark Indigo (#23003F) + lavanda (#C4B2D9), aplicada sobre el estilo retro
/// Windows 95 (relieves, trama, fuente) sin cambiarlo. Es la única fuente de los valores hexadecimales:
/// <c>Retro95Theme.xaml</c> la lee con <c>x:Static</c> y el menú de la bandeja (WinForms) con <see cref="ToDrawing"/>.
/// Contrastes WCAG verificados (texto normal, mínimo AA 4.5:1):
/// Text/Surface 11.6:1 · Text/Window 16.4:1 · OnPrimary/Primary 9.4:1 · OnPrimary/Indigo700 6.5:1 (fin de la barra de título).
/// </summary>
public static class RetroPalette
{
    // Escala tonal de Dark Indigo (tono 273°). 900 es el color base; 300 es la lavanda de acento.
    public static readonly Color Indigo50 = Color.FromRgb(0xF5, 0xF0, 0xF9);
    public static readonly Color Indigo100 = Color.FromRgb(0xE9, 0xDF, 0xF1);
    public static readonly Color Indigo200 = Color.FromRgb(0xD8, 0xC7, 0xE6);
    public static readonly Color Indigo300 = Color.FromRgb(0xC4, 0xB2, 0xD9);
    public static readonly Color Indigo400 = Color.FromRgb(0xA1, 0x7F, 0xBD);
    public static readonly Color Indigo500 = Color.FromRgb(0x7F, 0x4E, 0xA6);
    public static readonly Color Indigo600 = Color.FromRgb(0x60, 0x2E, 0x8A);
    public static readonly Color Indigo700 = Color.FromRgb(0x4A, 0x15, 0x75);
    public static readonly Color Indigo800 = Color.FromRgb(0x35, 0x05, 0x5C);
    public static readonly Color Indigo900 = Color.FromRgb(0x23, 0x00, 0x3F);

    // Nombres de color
    public static Color DarkIndigo => Indigo900;
    public static Color Lavender => Indigo300;

    // Nombres semánticos
    /// <summary>Selección, barra de título, elemento activo.</summary>
    public static Color Primary => Indigo900;
    /// <summary>Texto sobre <see cref="Primary"/> y otros fondos oscuros.</summary>
    public static Color OnPrimary => Lavender;
    /// <summary>Acento (lavanda).</summary>
    public static Color Accent => Lavender;
    /// <summary>Extremo claro del degradado de la barra de título (sigue cumpliendo AA con <see cref="OnPrimary"/>).</summary>
    public static Color PrimaryGradientEnd => Indigo700;
    /// <summary>Cara de ventanas, botones y menús (el "gris" clásico).</summary>
    public static Color Surface => Indigo200;
    /// <summary>Fondo de campos de texto, listas y casillas.</summary>
    public static Color Background => Indigo50;
    /// <summary>Texto normal, marcas de verificación, flechas y rectángulo de foco.</summary>
    public static Color Text => Indigo900;
    /// <summary>Texto deshabilitado (WCAG no exige contraste a controles deshabilitados).</summary>
    public static Color TextDisabled => Indigo500;

    // Relieve 3D clásico (de más claro a más oscuro)
    public static Color BevelHighlight => Indigo50;
    public static Color BevelLight => Indigo100;
    public static Color BevelShadow => Indigo500;
    public static Color BevelDarkShadow => Indigo900;

    /// <summary>Convierte a System.Drawing para el menú de la bandeja (WinForms).</summary>
    public static System.Drawing.Color ToDrawing(Color c) => System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
}
