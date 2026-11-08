using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using ReplayClip.Core;

namespace ReplayClip.Audio;

/// <summary>
/// Recibe avisos del sistema de audio (IMMNotificationClient): cambio del dispositivo predeterminado,
/// conexión o desconexión de auriculares y micrófonos. Es una suscripción pública de notificaciones
/// del sistema, no un hook.
/// </summary>
public sealed class AudioDeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private bool _registered;

    public AudioDeviceWatcher()
    {
        _registered = _enumerator.RegisterEndpointNotificationCallback(this) == 0;
    }

    /// <summary>Cambió el dispositivo predeterminado de salida (true) o de entrada (false).</summary>
    public event Action<bool>? DefaultDeviceChanged;

    /// <summary>Se conectó o desconectó algún dispositivo.</summary>
    public event Action? DevicesChanged;

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) Raise(true);
        else if (flow == DataFlow.Capture && role == Role.Console) Raise(false);
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => RaiseDevicesChanged();

    public void OnDeviceAdded(string pwstrDeviceId) => RaiseDevicesChanged();

    public void OnDeviceRemoved(string deviceId) => RaiseDevicesChanged();

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        if (_registered) _enumerator.UnregisterEndpointNotificationCallback(this);
        _registered = false;
        _enumerator.Dispose();
    }

    private void Raise(bool render)
    {
        Logger.Info($"Cambió el dispositivo de {(render ? "salida" : "entrada")} predeterminado.");
        // Se avisa desde el pool: no se debe llamar a WASAPI dentro de esta notificación.
        ThreadPool.QueueUserWorkItem(_ => DefaultDeviceChanged?.Invoke(render));
    }

    private void RaiseDevicesChanged() => ThreadPool.QueueUserWorkItem(_ => DevicesChanged?.Invoke());
}
