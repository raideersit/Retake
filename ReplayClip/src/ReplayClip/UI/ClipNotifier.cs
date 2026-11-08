using System.Diagnostics;
using System.Windows.Forms;
using ReplayClip.Core;

namespace ReplayClip.UI;

/// <summary>
/// Notificaciones del sistema (en Windows 10 el globo de NotifyIcon se muestra como notificación del
/// Centro de actividades). Al hacer clic en la de un clip se abre el Explorador con el archivo seleccionado.
/// </summary>
public sealed class ClipNotifier
{
    private readonly NotifyIcon _notifyIcon;
    private string? _lastPath;

    public ClipNotifier(NotifyIcon notifyIcon)
    {
        _notifyIcon = notifyIcon;
        _notifyIcon.BalloonTipClicked += (_, _) => RevealLastFile();
    }

    /// <summary>Muestra un aviso. Llamar desde el hilo de UI.</summary>
    public void Show(EngineNotification notification)
    {
        _lastPath = notification.FilePath;
        var icon = notification.Kind switch
        {
            NotificationKind.Error => ToolTipIcon.Error,
            NotificationKind.Warning => ToolTipIcon.Warning,
            _ => ToolTipIcon.Info,
        };
        string message = notification.Message;
        if (notification.FilePath is not null && notification.Kind == NotificationKind.Success)
        {
            message = $"{notification.FilePath}\nHaz clic para abrir la carpeta.";
        }
        _notifyIcon.ShowBalloonTip(4000, notification.Title, message, icon);
    }

    /// <summary>Abre el Explorador con el último archivo guardado seleccionado.</summary>
    public void RevealLastFile()
    {
        if (_lastPath is null || !File.Exists(_lastPath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastPath}\"") { UseShellExecute = true });
    }
}
