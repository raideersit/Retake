using ReplayClip.Buffer;
using ReplayClip.Core;

namespace ReplayClip.Muxing;

/// <summary>
/// Convierte una instantánea del búfer en un MP4 sin recodificar. Escribe primero un ".part" y lo
/// renombra al terminar: si falla (disco lleno, cierre), nunca queda un .mp4 corrupto con nombre final.
/// Trabaja en un hilo de baja prioridad para no competir con el juego.
/// </summary>
public static class ClipExporter
{
    /// <summary>Margen de espacio libre exigido además del tamaño del clip.</summary>
    private const long SafetyMarginBytes = 64L * 1024 * 1024;

    /// <summary>Exporta y desecha la instantánea. Devuelve la ruta del clip.</summary>
    public static Task<string> ExportAsync(ReplaySnapshot snapshot, string folder)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(Export(snapshot, folder)); }
            catch (Exception ex) { completion.SetException(ex); }
            finally { snapshot.Dispose(); }
        })
        {
            IsBackground = false, // que un cierre de la app no corte el clip a medias
            Name = "ReplayClip.ClipExporter",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
        return completion.Task;
    }

    /// <summary>Exportación síncrona (también la usan las pruebas).</summary>
    public static string Export(ReplaySnapshot snapshot, string folder)
    {
        Directory.CreateDirectory(folder);
        EnsureFreeSpace(folder, snapshot.TotalBytes);

        string path = UniquePath(folder, $"Clip_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}", ".mp4");
        string partial = path + ".part";
        var audioFormats = snapshot.Audio.OrderBy(a => a.Key).Select(a => a.Value.Format).ToList();

        try
        {
            using (var muxer = new Mp4Muxer(partial, snapshot.VideoFormat, audioFormats, snapshot.StartPts, fragmented: false))
            {
                // Intercalado por tiempo: el MP4 queda ordenado y se reproduce bien al hacer streaming.
                var all = snapshot.Video
                    .Concat(snapshot.Audio.Values.SelectMany(a => a.Samples))
                    .OrderBy(s => s.Info.Pts)
                    .ThenBy(s => s.Info.Kind);
                foreach (var sample in all) muxer.WriteSample(sample.Info, sample.Data);
                muxer.FinalizeFile();
            }
            File.Move(partial, path);
            Logger.Info($"Clip guardado: {path} ({snapshot.DurationSeconds:F1} s, {snapshot.TotalBytes / 1024 / 1024} MB, " +
                        $"{snapshot.Video.Count} fotogramas, pistas de audio: {string.Join(", ", snapshot.Audio.Keys)}).");
            return path;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>Lanza una excepción clara si el disco no tiene sitio para <paramref name="bytes"/>.</summary>
    public static void EnsureFreeSpace(string folder, long bytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root)) return;
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < bytes + SafetyMarginBytes)
            {
                throw new IOException($"No hay espacio suficiente en {drive.Name}: hacen falta " +
                                      $"{(bytes + SafetyMarginBytes) / 1024 / 1024} MB y quedan {drive.AvailableFreeSpace / 1024 / 1024} MB.");
            }
        }
        catch (ArgumentException) { /* rutas de red u otras sin letra: no se comprueba */ }
    }

    public static string UniquePath(string folder, string baseName, string extension)
    {
        string path = Path.Combine(folder, baseName + extension);
        for (int i = 2; File.Exists(path) || File.Exists(path + ".part"); i++)
        {
            path = Path.Combine(folder, $"{baseName}_{i}{extension}");
        }
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException ex) { Logger.Warn($"No se pudo borrar {path}: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { Logger.Warn($"No se pudo borrar {path}: {ex.Message}"); }
    }
}
