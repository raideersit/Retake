using System.Text.Json;
using System.Text.Json.Serialization;
using ReplayClip.Core;

namespace ReplayClip.Settings;

/// <summary>
/// Carga y guarda los ajustes en %APPDATA%\ReplayClip\settings.json. La escritura es atómica
/// (archivo temporal + reemplazo) para que un corte de luz no deje un JSON a medias.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public SettingsStore(string? path = null)
    {
        FilePath = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReplayClip", "settings.json");
    }

    public string FilePath { get; }

    /// <summary>Lee los ajustes; si no existen o están dañados, devuelve los valores por defecto.</summary>
    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (settings is not null) return settings.Normalize();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Logger.Warn($"No se pudieron leer los ajustes ({ex.Message}); se usan los valores por defecto.");
        }
        return new AppSettings().Normalize();
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        string? directory = Path.GetDirectoryName(FilePath);
        if (directory is not null) Directory.CreateDirectory(directory);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }
}
