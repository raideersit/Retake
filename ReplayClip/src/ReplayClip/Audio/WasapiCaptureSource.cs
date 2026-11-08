using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ReplayClip.Core;

namespace ReplayClip.Audio;

/// <summary>
/// Una fuente WASAPI en modo compartido: el audio del sistema (loopback del dispositivo de salida
/// predeterminado) o un micrófono. Se usa la API de bajo nivel de NAudio (AudioClient) en lugar de
/// WasapiCapture porque necesitamos la marca QPC de cada paquete para sincronizar con el video.
/// No tiene hilo propio: <see cref="AudioPipeline"/> la consulta cada 10 ms.
/// Si el dispositivo desaparece, sigue entregando silencio y reintenta abrirlo cada 2 s.
/// </summary>
public sealed class WasapiCaptureSource : IDisposable
{
    /// <summary>Búfer de WASAPI: 200 ms (holgado para un sondeo cada 10 ms).</summary>
    private const long BufferDuration = 2_000_000;

    /// <summary>Retraso con el que se rellena silencio cuando la fuente no entrega nada.</summary>
    private static readonly long SilenceLatencyFrames = PcmTimeline.SampleRate * 150 / 1000;

    private readonly bool _loopback;
    private readonly string? _deviceId;
    private readonly short[] _scratch = new short[PcmTimeline.SampleRate * PcmTimeline.Channels];
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private AudioClient? _client;
    private AudioCaptureClient? _capture;
    private PcmConverter? _converter;
    private DateTime _nextRetry = DateTime.MinValue;
    private volatile bool _reopenRequested;

    /// <param name="loopback">True = audio del sistema; false = micrófono.</param>
    /// <param name="deviceId">Id del dispositivo, o null para el predeterminado.</param>
    public WasapiCaptureSource(bool loopback, string? deviceId, string label)
    {
        _loopback = loopback;
        _deviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
        Label = label;
    }

    public string Label { get; }

    public PcmTimeline Timeline { get; } = new();

    public bool IsOpen => _capture is not null;

    /// <summary>Nombre del dispositivo abierto (para el registro).</summary>
    public string? DeviceName { get; private set; }

    /// <summary>True si existe algún dispositivo para esta fuente (p. ej. hay micrófono conectado).</summary>
    public static bool DeviceExists(bool loopback, string? deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!string.IsNullOrEmpty(deviceId))
            {
                using var device = enumerator.GetDevice(deviceId);
                return device.State == DeviceState.Active;
            }
            return enumerator.HasDefaultAudioEndpoint(loopback ? DataFlow.Render : DataFlow.Capture, loopback ? Role.Multimedia : Role.Console);
        }
        catch (COMException)
        {
            return false;
        }
    }

    /// <summary>Pide reabrir el dispositivo (cambió el predeterminado). Seguro desde cualquier hilo.</summary>
    public void RequestReopen() => _reopenRequested = true;

    /// <summary>Lee todo lo disponible y lo añade a <see cref="Timeline"/>.</summary>
    public void Poll()
    {
        if (_reopenRequested && _deviceId is null)
        {
            _reopenRequested = false;
            Close();
            _nextRetry = DateTime.MinValue;
        }
        if (_capture is null && DateTime.UtcNow >= _nextRetry) TryOpen();

        if (_capture is not null)
        {
            try
            {
                ReadPackets();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // AUDCLNT_E_DEVICE_INVALIDATED (desconectado), servicio de audio reiniciado, etc.
                Logger.Warn($"{Label}: se perdió el dispositivo ({ex.Message}); se reintentará.");
                Close();
                _nextRetry = DateTime.UtcNow.AddSeconds(2);
            }
        }

        // Si no llega nada (nada sonando, o dispositivo ausente) se avanza con silencio.
        Timeline.FillSilenceUntil(PcmTimeline.TicksToIndex(MediaClock.Now) - SilenceLatencyFrames);
    }

    public void Dispose()
    {
        Close();
        _enumerator?.Dispose();
        _enumerator = null;
    }

    private void TryOpen()
    {
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            _device = GetDevice();
            if (_device is null) throw new InvalidOperationException("No hay dispositivo.");
            DeviceName = _device.FriendlyName;

            var flags = _loopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None;
            var desired = new WaveFormat(PcmTimeline.SampleRate, 16, PcmTimeline.Channels);
            _client = _device.AudioClient;
            try
            {
                // Windows convierte a 48 kHz estéreo 16 bits por nosotros: cero trabajo de CPU propio.
                _client.Initialize(AudioClientShareMode.Shared,
                    flags | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                    BufferDuration, 0, desired, Guid.Empty);
                _converter = null;
            }
            catch (COMException)
            {
                // Sin conversión automática: se abre en el formato de mezcla y se convierte aquí.
                _client.Dispose();
                _device.Dispose();
                _device = GetDevice() ?? throw new InvalidOperationException("No hay dispositivo.");
                _client = _device.AudioClient;
                var mix = _client.MixFormat;
                _client.Initialize(AudioClientShareMode.Shared, flags, BufferDuration, 0, mix, Guid.Empty);
                _converter = new PcmConverter(mix);
                Logger.Info($"{Label}: conversión propia desde {mix}.");
            }
            _capture = _client.AudioCaptureClient;
            _client.Start();
            Logger.Info($"{Label}: capturando de \"{DeviceName}\".");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException)
        {
            if (_nextRetry == DateTime.MinValue) Logger.Warn($"{Label}: no se pudo abrir el dispositivo ({ex.Message}).");
            Close();
            _nextRetry = DateTime.UtcNow.AddSeconds(2);
        }
    }

    private MMDevice? GetDevice()
    {
        if (_deviceId is not null) return _enumerator!.GetDevice(_deviceId);
        var flow = _loopback ? DataFlow.Render : DataFlow.Capture;
        var role = _loopback ? Role.Multimedia : Role.Console;
        return _enumerator!.HasDefaultAudioEndpoint(flow, role) ? _enumerator.GetDefaultAudioEndpoint(flow, role) : null;
    }

    private void ReadPackets()
    {
        var capture = _capture!;
        int packet = capture.GetNextPacketSize();
        while (packet > 0)
        {
            IntPtr data = capture.GetBuffer(out int frames, out AudioClientBufferFlags bufferFlags, out _, out long qpcPosition);
            try
            {
                bool silent = (bufferFlags & AudioClientBufferFlags.Silent) != 0;
                // qpcPosition ya viene en unidades de 100 ns del QPC: misma base que el video.
                long qpc = (bufferFlags & AudioClientBufferFlags.TimestampError) != 0 ? -1 : qpcPosition;
                Timeline.Append(qpc, Convert(data, frames, silent));
            }
            finally
            {
                capture.ReleaseBuffer(frames);
            }
            packet = capture.GetNextPacketSize();
        }
    }

    private unsafe ReadOnlySpan<short> Convert(IntPtr data, int frames, bool silent)
    {
        if (_converter is not null) return _converter.Convert(data, frames, silent);
        int count = Math.Min(frames * PcmTimeline.Channels, _scratch.Length);
        var span = _scratch.AsSpan(0, count);
        if (silent) span.Clear();
        else new ReadOnlySpan<short>((void*)data, count).CopyTo(span);
        return span;
    }

    private void Close()
    {
        try { _client?.Stop(); } catch (COMException) { }
        _capture?.Dispose();
        _client?.Dispose();
        _device?.Dispose();
        _capture = null;
        _client = null;
        _device = null;
    }
}
