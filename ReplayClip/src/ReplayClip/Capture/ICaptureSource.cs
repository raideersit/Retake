using Vortice.Direct3D11;

namespace ReplayClip.Capture;

/// <summary>
/// Receptor de fotogramas capturados. La textura (BGRA, en la GPU) solo es válida durante la llamada.
/// </summary>
/// <param name="texture">Imagen capturada.</param>
/// <param name="contentWidth">Ancho útil (puede ser menor que la textura).</param>
/// <param name="contentHeight">Alto útil.</param>
/// <param name="systemTime">Instante de la imagen en 100 ns del dominio QPC.</param>
public delegate void CaptureFrameHandler(ID3D11Texture2D texture, int contentWidth, int contentHeight, long systemTime);

/// <summary>Fuente de captura: Windows.Graphics.Capture o Desktop Duplication.</summary>
public interface ICaptureSource : IDisposable
{
    /// <summary>El objetivo dejó de existir (ventana cerrada, monitor desconectado).</summary>
    event Action<CaptureTarget>? TargetClosed;

    /// <summary>Objetivo que se está capturando, o null si está detenida.</summary>
    CaptureTarget? CurrentTarget { get; }

    /// <summary>Nombre del método para la interfaz y el registro.</summary>
    string MethodName { get; }

    /// <summary>Empieza a capturar <paramref name="target"/>. Lanza una excepción si no es posible.</summary>
    void Start(CaptureTarget target);

    /// <summary>Detiene la captura sin liberar el objeto.</summary>
    void Stop();
}
