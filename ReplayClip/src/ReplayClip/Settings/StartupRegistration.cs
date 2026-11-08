using Microsoft.Win32;
using ReplayClip.Core;

namespace ReplayClip.Settings;

/// <summary>
/// Inicio con Windows mediante HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// No requiere permisos de administrador ni servicios.
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ReplayClip";

    /// <summary>Argumento con el que se arranca desde el registro (sin abrir la ventana de ajustes).</summary>
    public const string StartupArgument = "--startup";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Ruta del ejecutable desconocida.");
                key.SetValue(ValueName, $"\"{exe}\" {StartupArgument}");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Logger.Error("No se pudo cambiar el inicio con Windows", ex);
        }
    }
}
