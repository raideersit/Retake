using System.Buffers;
using System.Collections.Concurrent;
using ReplayClip.Core;

namespace ReplayClip.Muxing;

/// <summary>
/// Grabación continua a disco en streaming: cada muestra se copia a una cola acotada y un hilo la escribe
/// en un MP4 fragmentado. Nada se acumula en RAM salvo la cola (límite de 96 MB).
/// <para>Disco lleno o demasiado lento: la grabación se detiene sola y el archivo queda reproducible.</para>
/// <para>Si el codificador se reinicia (nueva generación de formato), se cierra la parte actual y se abre otra.</para>
/// </summary>
public sealed class RecordingWriter : IEncodedSampleSink, IDisposable
{
    private const long MaxQueuedBytes = 96L * 1024 * 1024;
    private const long MinFreeDiskBytes = 256L * 1024 * 1024;

    private readonly string _folder;
    private readonly Func<StreamKind, StreamFormat?> _formatLookup;
    private readonly BlockingCollection<QueuedSample> _queue = new(new ConcurrentQueue<QueuedSample>());
    private readonly Thread _thread;
    private readonly string _baseName;
    private readonly List<string> _completedFiles = new();
    private long _queuedBytes;
    private volatile bool _faulted;
    private Mp4Muxer? _muxer;
    private int _currentGeneration = -1;
    private int _part;
    private DateTime _nextDiskCheck = DateTime.MinValue;

    private readonly record struct QueuedSample(EncodedSampleInfo Info, byte[] Buffer, int Length);

    public RecordingWriter(string folder, Func<StreamKind, StreamFormat?> formatLookup)
    {
        _folder = folder;
        _formatLookup = formatLookup;
        _baseName = $"Grabacion_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        Directory.CreateDirectory(folder);
        ClipExporter.EnsureFreeSpace(folder, MinFreeDiskBytes);
        _thread = new Thread(WriterLoop) { IsBackground = false, Name = "ReplayClip.RecordingWriter", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
        StartedAt = DateTime.Now;
        Logger.Info("Grabación continua iniciada.");
    }

    /// <summary>La grabación se detuvo sola por un error (disco lleno...). Se invoca en el hilo de escritura.</summary>
    public event Action<Exception>? Faulted;

    public DateTime StartedAt { get; }

    /// <summary>Archivos terminados (una grabación puede tener varias partes).</summary>
    public IReadOnlyList<string> CompletedFiles => _completedFiles;

    public void OnFormat(StreamFormat format) { /* se consultan al abrir cada parte */ }

    public void OnSample(in EncodedSampleInfo info, ReadOnlySpan<byte> data)
    {
        if (_faulted || _queue.IsAddingCompleted) return;
        if (Interlocked.Read(ref _queuedBytes) + data.Length > MaxQueuedBytes)
        {
            Fault(new IOException("El disco no escribe lo bastante rápido; la grabación se detuvo."));
            return;
        }
        byte[] buffer = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(buffer);
        Interlocked.Add(ref _queuedBytes, data.Length);
        try
        {
            _queue.Add(new QueuedSample(info, buffer, data.Length));
        }
        catch (InvalidOperationException)
        {
            ArrayPool<byte>.Shared.Return(buffer); // se cerró justo ahora
            Interlocked.Add(ref _queuedBytes, -data.Length);
        }
    }

    /// <summary>Termina de escribir lo pendiente y cierra el archivo. Devuelve las rutas generadas.</summary>
    public IReadOnlyList<string> Stop()
    {
        _queue.CompleteAdding();
        _thread.Join();
        return _completedFiles;
    }

    public void Dispose()
    {
        if (!_queue.IsAddingCompleted) Stop();
        _queue.Dispose();
    }

    /// <summary>
    /// Al arrancar: las grabaciones a medias (MP4 fragmentado, reproducibles) se renombran a "_recuperado.mp4";
    /// los clips a medias (MP4 sin índice, irrecuperables) se borran.
    /// </summary>
    public static void RecoverIncompleteFiles(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (string partial in Directory.EnumerateFiles(folder, "*.mp4.part"))
        {
            try
            {
                string name = Path.GetFileName(partial);
                if (name.StartsWith("Grabacion_", StringComparison.OrdinalIgnoreCase) && new FileInfo(partial).Length > 0)
                {
                    string recovered = ClipExporter.UniquePath(folder, Path.GetFileNameWithoutExtension(partial[..^5]) + "_recuperado", ".mp4");
                    File.Move(partial, recovered);
                    Logger.Info($"Grabación interrumpida recuperada: {recovered}");
                }
                else
                {
                    File.Delete(partial);
                    Logger.Info($"Clip incompleto eliminado: {partial}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"No se pudo revisar {partial}: {ex.Message}");
            }
        }
    }

    private void WriterLoop()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try
            {
                if (!_faulted) Write(item);
            }
            catch (Exception ex)
            {
                Fault(ex);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(item.Buffer);
                Interlocked.Add(ref _queuedBytes, -item.Length);
            }
        }
        CloseCurrentPart();
        Logger.Info($"Grabación continua terminada: {string.Join(", ", _completedFiles)}");
    }

    private void Write(in QueuedSample item)
    {
        var info = item.Info;
        bool isVideo = info.Kind == StreamKind.Video;

        // Una parte nueva empieza siempre en un fotograma clave de la generación vigente.
        if (isVideo && info.Generation != _currentGeneration)
        {
            if (!info.IsKeyFrame) return;
            CloseCurrentPart();
            OpenPart(info);
        }
        if (_muxer is null) return; // audio anterior al primer fotograma clave

        _muxer.WriteSample(info, item.Buffer.AsSpan(0, item.Length));

        if (DateTime.UtcNow >= _nextDiskCheck)
        {
            _nextDiskCheck = DateTime.UtcNow.AddSeconds(5);
            ClipExporter.EnsureFreeSpace(_folder, MinFreeDiskBytes);
        }
    }

    private void OpenPart(in EncodedSampleInfo firstKeyFrame)
    {
        var video = _formatLookup(StreamKind.Video);
        if (video is null || video.Generation != firstKeyFrame.Generation) return;
        var audio = new[] { StreamKind.AudioMix, StreamKind.AudioSystem, StreamKind.AudioMic }
            .Select(_formatLookup).OfType<StreamFormat>().ToList();

        _part++;
        string baseName = _part == 1 ? _baseName : $"{_baseName}_parte{_part}";
        string path = ClipExporter.UniquePath(_folder, baseName, ".mp4");
        _muxer = new Mp4Muxer(path + ".part", video, audio, firstKeyFrame.Pts, fragmented: true);
        _currentGeneration = firstKeyFrame.Generation;
        Logger.Info($"Grabando en {path} ({video.Description}; {audio.Count} pistas de audio).");
    }

    private void CloseCurrentPart()
    {
        if (_muxer is null) return;
        string partial = _muxer.Path;
        try
        {
            _muxer.FinalizeFile();
        }
        catch (Exception ex)
        {
            // Con MP4 fragmentado lo escrito hasta el último fragmento sigue siendo reproducible.
            Logger.Error("No se pudo finalizar la grabación (se conserva lo escrito)", ex);
        }
        finally
        {
            _muxer.Dispose();
            _muxer = null;
        }
        try
        {
            string final = partial[..^5]; // quita ".part"
            File.Move(partial, final);
            _completedFiles.Add(final);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error($"No se pudo renombrar {partial}", ex);
            _completedFiles.Add(partial);
        }
    }

    private void Fault(Exception ex)
    {
        if (_faulted) return;
        _faulted = true;
        Logger.Error("La grabación continua se detuvo por un error", ex);
        _queue.CompleteAdding();
        try { Faulted?.Invoke(ex); }
        catch (Exception handlerError) { Logger.Error("Error en el manejador de fallo de grabación", handlerError); }
    }
}
