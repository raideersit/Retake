using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReplayClip.Core;
using ReplayClip.Settings;
using ReplayClip.UI;

namespace ReplayClip.SmokeTest;

/// <summary>
/// Dibuja la interfaz a PNG sin mostrarla en pantalla: ventana de Ajustes (fuera de la vista),
/// menú de la bandeja e iconos ampliados. Sirve para revisar el diseño sin tocar el escritorio.
/// </summary>
public static class UiPreview
{
    public static void Render(string folder)
    {
        Directory.CreateDirectory(folder);
        var thread = new Thread(() => RenderOnUiThread(folder));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    private static void RenderOnUiThread(string folder)
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        var settings = new AppSettings().Normalize();
        var engine = new RecorderEngine(settings); // sin Start(): solo para el texto de estado
        var window = new SettingsWindow(settings, engine)
        {
            Left = -30000,
            Top = -30000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        SavePng(content, Path.Combine(folder, "ajustes.png"));
        window.Close();

        using (var tray = new TrayIconController())
        {
            var menu = tray.Menu;
            tray.UpdateState(false, "Ctrl+Shift+9", "Ctrl+Shift+0", 30);
            menu.Show(-30000, -30000);
            using var bitmap = new System.Drawing.Bitmap(menu.Width, menu.Height);
            menu.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, menu.Width, menu.Height));
            menu.Close();
            bitmap.Save(Path.Combine(folder, "menu.png"), System.Drawing.Imaging.ImageFormat.Png);
        }

        // Iconos ampliados ×4 con vecino más cercano para ver los píxeles.
        using var preview = new System.Drawing.Bitmap(32 * 4 * 2 + 16, 32 * 4);
        using (var g = System.Drawing.Graphics.FromImage(preview))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.Clear(System.Drawing.Color.FromArgb(0x00, 0x80, 0x80)); // escritorio verde azulado clásico
            int x = 0;
            foreach (bool recording in new[] { false, true })
            {
                using var icon = TrayIconController.CreatePixelIcon(recording);
                using var image = icon.ToBitmap();
                g.DrawImage(image, x, 0, 128, 128);
                x += 128 + 16;
            }
        }
        preview.Save(Path.Combine(folder, "iconos.png"), System.Drawing.Imaging.ImageFormat.Png);
        engine.Dispose();
        Console.WriteLine($"Vista previa guardada en {folder}");
    }

    private static void SavePng(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
