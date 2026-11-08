using ReplayClip.Core;

namespace ReplayClip.Capture;

/// <summary>
/// Controla el ritmo de fotogramas hacia el codificador:
/// <list type="bullet">
/// <item>Descarta fotogramas sobrantes (monitor a 144 Hz grabando a 60 fps) con coste casi nulo.</item>
/// <item>Windows.Graphics.Capture solo entrega fotogramas cuando el contenido cambia; si la imagen está
/// quieta (o la ventana minimizada), un temporizador de alta resolución repite el último fotograma para
/// mantener 30/60 fps constantes y fotogramas clave exactos cada segundo.</item>
/// </list>
/// </summary>
public sealed class FramePacer : IDisposable
{
    private readonly long _interval;
    private readonly Action<long> _duplicateRequested;
    private readonly Thread _watchdog;
    private readonly HighResolutionTimer _timer = new();
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly object _gate = new();
    private long _nextDue;
    private long _lastPts;
    private long _lastSubmitClock;

    /// <param name="framesPerSecond">30 o 60.</param>
    /// <param name="duplicateRequested">Se invoca (en el hilo del vigilante) con el PTS para repetir el último fotograma.</param>
    public FramePacer(int framesPerSecond, Action<long> duplicateRequested)
    {
        FramesPerSecond = framesPerSecond;
        _interval = MediaClock.TicksPerSecond / framesPerSecond;
        _duplicateRequested = duplicateRequested;
        _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "ReplayClip.FramePacer", Priority = ThreadPriority.AboveNormal };
        _watchdog.Start();
    }

    public int FramesPerSecond { get; }

    /// <summary>Duración de un fotograma en 100 ns.</summary>
    public long FrameDuration => _interval;

    /// <summary>
    /// Decide si un fotograma capturado en <paramref name="captureTime"/> debe codificarse y, si es así,
    /// devuelve su PTS (siempre creciente).
    /// </summary>
    public bool TryAccept(long captureTime, out long pts)
    {
        lock (_gate)
        {
            pts = 0;
            // Se acepta si llega como mucho medio intervalo antes de su turno.
            if (_nextDue != 0 && captureTime < _nextDue - _interval / 2) return false;
            _nextDue = _nextDue == 0 ? captureTime + _interval : _nextDue + _interval;
            // Tras una pausa larga (ventana minimizada) se resincroniza en lugar de acumular deuda.
            if (_nextDue < captureTime - _interval) _nextDue = captureTime + _interval;
            pts = MonotonicPts(captureTime);
            return true;
        }
    }

    /// <summary>Reserva un PTS creciente para un fotograma generado (p. ej. imagen en negro).</summary>
    public long ClaimPts(long candidate)
    {
        lock (_gate)
        {
            _nextDue = candidate + _interval;
            return MonotonicPts(candidate);
        }
    }

    /// <summary>Anota que se envió un fotograma (real o repetido) para que el vigilante no lo duplique.</summary>
    public void MarkSubmitted()
    {
        Volatile.Write(ref _lastSubmitClock, MediaClock.Now);
    }

    public void Dispose()
    {
        _stop.Set();
        _watchdog.Join(1000);
        _timer.Dispose();
        _stop.Dispose();
    }

    private long MonotonicPts(long candidate)
    {
        // Al menos 1 ms entre fotogramas para que el muxer nunca vea marcas iguales o hacia atrás.
        long minimum = _lastPts + MediaClock.TicksPerSecond / 1000;
        _lastPts = Math.Max(candidate, minimum);
        return _lastPts;
    }

    private void WatchdogLoop()
    {
        while (!_stop.IsSet)
        {
            WaitInterval();
            if (_stop.IsSet) break;
            long now = MediaClock.Now;
            long last = Volatile.Read(ref _lastSubmitClock);
            if (last == 0 || now - last < _interval + _interval / 2) continue;

            long pts;
            lock (_gate)
            {
                pts = MonotonicPts(now);
                _nextDue = now + _interval;
            }
            try { _duplicateRequested(pts); }
            catch (Exception ex) { Logger.Error("Error repitiendo fotograma", ex); }
        }
    }

    private void WaitInterval() => _timer.Wait(_interval);
}
