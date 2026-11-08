using System.Media;
using ReplayClip.Core;

namespace ReplayClip.UI;

/// <summary>
/// Sonidos cortos de confirmación, sintetizados en memoria (sin archivos de audio). Se reproducen por la
/// salida predeterminada, así que quedan grabados en el audio del sistema: por eso son breves y suaves,
/// y se pueden desactivar en Ajustes. (Windows 11 permitiría excluir el audio propio de la captura;
/// Windows 10 no).
/// </summary>
public sealed class SoundCues : IDisposable
{
    private const int SampleRate = 44_100;
    private const double Volume = 0.22;

    private readonly Dictionary<NotificationSound, SoundPlayer> _players = new();

    public SoundCues()
    {
        _players[NotificationSound.ClipSaved] = Create(Build((880, 70), (0, 25), (1318.5, 110)));
        _players[NotificationSound.RecordingStarted] = Create(Build((1174.7, 120)));
        _players[NotificationSound.RecordingStopped] = Create(Build((1174.7, 70), (0, 25), (784, 110)));
        _players[NotificationSound.Error] = Create(Build((330, 220)));
    }

    /// <summary>Reproduce un sonido sin bloquear.</summary>
    public void Play(NotificationSound sound)
    {
        if (!_players.TryGetValue(sound, out var player)) return;
        try { player.Play(); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            Logger.Warn($"No se pudo reproducir el sonido {sound}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        foreach (var player in _players.Values)
        {
            player.Stream?.Dispose();
            player.Dispose();
        }
        _players.Clear();
    }

    /// <summary>
    /// Genera un WAV PCM 16 bits mono con una secuencia de tonos (frecuencia en Hz, 0 = silencio; duración en ms).
    /// Cada tono lleva ataque y caída de 8 ms para que no haga "clic".
    /// </summary>
    public static byte[] Build(params (double Frequency, int Milliseconds)[] tones)
    {
        var samples = new List<short>();
        int ramp = SampleRate * 8 / 1000;
        foreach (var (frequency, milliseconds) in tones)
        {
            int count = SampleRate * milliseconds / 1000;
            for (int i = 0; i < count; i++)
            {
                double envelope = Math.Min(1.0, Math.Min(i, count - 1 - i) / (double)ramp);
                double value = frequency <= 0 ? 0 : Math.Sin(2 * Math.PI * frequency * i / SampleRate) * envelope * Volume;
                samples.Add((short)(value * short.MaxValue));
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int dataBytes = samples.Count * 2;
            writer.Write("RIFF"u8);
            writer.Write(36 + dataBytes);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);                 // tamaño del bloque fmt
            writer.Write((short)1);           // PCM
            writer.Write((short)1);           // mono
            writer.Write(SampleRate);
            writer.Write(SampleRate * 2);     // bytes por segundo
            writer.Write((short)2);           // alineación de bloque
            writer.Write((short)16);          // bits por muestra
            writer.Write("data"u8);
            writer.Write(dataBytes);
            foreach (short sample in samples) writer.Write(sample);
        }
        return stream.ToArray();
    }

    private static SoundPlayer Create(byte[] wav)
    {
        var player = new SoundPlayer(new MemoryStream(wav));
        player.Load(); // carga ahora para que Play() sea instantáneo
        return player;
    }
}
