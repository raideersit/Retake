using System.Runtime.InteropServices;
using ReplayClip.Capture;
using ReplayClip.Core;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace ReplayClip.Video;

/// <summary>Parámetros de codificación de video.</summary>
public sealed record VideoEncoderSettings(int Width, int Height, int FramesPerSecond, int BitrateBitsPerSecond);

/// <summary>
/// Codificador H.264 sobre un MFT de Media Foundation.
/// <para>Hardware (Quick Sync / NVENC / AMF): MFT asíncrono con IMFDXGIDeviceManager; recibe la textura
/// NV12 de la GPU directamente, sin copiarla a la CPU.</para>
/// <para>Software (respaldo): MFT síncrono de Microsoft; obliga a leer la textura a memoria (más CPU).</para>
/// Configuración: CBR, sin B-frames (DTS = PTS), GOP = fps (un fotograma clave por segundo), baja latencia.
/// </summary>
public sealed class H264Encoder : IDisposable
{
    /// <summary>Callback de salida: metadatos + bytes Annex-B.</summary>
    public delegate void SampleCallback(in EncodedSampleInfo info, ReadOnlySpan<byte> data);

    private const int MfENotAccepting = unchecked((int)0xC00D36B5);
    private const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    private const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
    private const int MaxFramesInFlight = 3;
    private static readonly Guid IidTexture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private readonly D3DDeviceManager _device;
    private readonly VideoEncoderSettings _settings;
    private readonly int _generation;
    private readonly Action<StreamFormat> _onFormat;
    private readonly SampleCallback _onSample;
    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator? _events;
    private readonly CodecApi? _codecApi;
    private const int MfENoEventsAvailable = unchecked((int)0xC00D3E80);

    private readonly object _pendingLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly long _frameDuration;
    private ID3D11Texture2D? _staging;

    private (Nv12Slot Slot, long Pts)? _pending;
    private int _needInput;
    private long _submitted;
    private long _completed;
    private long _lastKeyFramePts;
    private long _needInputEvents;
    private long _haveOutputEvents;
    private long _replacedPending;
    private long _lastForcedPts;
    private bool _formatPublished;
    private volatile bool _keyFrameRequested;
    private volatile bool _stopping;
    private bool _disposed;

    private H264Encoder(D3DDeviceManager device, VideoEncoderSettings settings, int generation, EncoderCandidate candidate,
        Action<StreamFormat> onFormat, SampleCallback onSample)
    {
        _device = device;
        _settings = settings;
        _generation = generation;
        _onFormat = onFormat;
        _onSample = onSample;
        _frameDuration = MediaClock.TicksPerSecond / settings.FramesPerSecond;
        Name = candidate.Name;
        IsHardware = candidate.IsHardware;

        _transform = candidate.Activate.ActivateObject<IMFTransform>();
        try
        {
            var attributes = _transform.Attributes;
            IsAsync = attributes.GetUInt32(TransformAttributeKeys.TransformAsync, out uint isAsync).Success && isAsync != 0;
            if (IsAsync) attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
            attributes.Set(CodecApiGuids.LowLatencyMode, 1u);

            bool d3dAware = attributes.GetUInt32(TransformAttributeKeys.D3D11Aware, out uint aware).Success && aware != 0;
            UsesGpuInput = IsHardware && d3dAware;
            if (UsesGpuInput)
            {
                _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)_device.MfDeviceManager.NativePointer);
            }

            _codecApi = CodecApi.From(_transform.NativePointer);
            ConfigureCodecApi();
            SetOutputType();
            SetInputType();

            if (IsAsync) _events = _transform.QueryInterface<IMFMediaEventGenerator>();
            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        }
        catch
        {
            _codecApi?.Dispose();
            _transform.Dispose();
            throw;
        }

        _thread = new Thread(EncoderLoop)
        {
            IsBackground = true,
            Name = "ReplayClip.H264Encoder",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        Logger.Info($"Codificador listo: {Name} ({(IsHardware ? "hardware" : "software")}, {(IsAsync ? "asíncrono" : "síncrono")}, " +
                    $"entrada {(UsesGpuInput ? "GPU sin copia" : "memoria de sistema")}) {settings.Width}x{settings.Height} " +
                    $"{settings.FramesPerSecond} fps {settings.BitrateBitsPerSecond / 1_000_000} Mbps.");
    }

    /// <summary>Se produjo un error irrecuperable (p. ej. pérdida del dispositivo).</summary>
    public event Action<Exception>? Failed;

    public string Name { get; }

    public bool IsHardware { get; }

    public bool IsAsync { get; }

    /// <summary>True si recibe texturas D3D11 directamente (cero copias).</summary>
    public bool UsesGpuInput { get; }

    /// <summary>Contadores de diagnóstico para el registro y la ventana de ajustes.</summary>
    public string Statistics =>
        $"enviados={Interlocked.Read(ref _submitted)} codificados={Interlocked.Read(ref _completed)} " +
        $"descartados={Interlocked.Read(ref _replacedPending)} NeedInput={Interlocked.Read(ref _needInputEvents)} " +
        $"HaveOutput={Interlocked.Read(ref _haveOutputEvents)} pendientesNeedInput={_needInput}";

    /// <summary>Fotogramas que el codificador ya devolvió codificados.</summary>
    public long CompletedFrames => Interlocked.Read(ref _completed);

    /// <summary>
    /// Crea el primer codificador que funcione: hardware de la GPU indicada y, si ninguno sirve, software.
    /// </summary>
    public static H264Encoder Create(D3DDeviceManager device, VideoEncoderSettings settings, int generation,
        Action<StreamFormat> onFormat, SampleCallback onSample, bool allowSoftware = true)
    {
        var candidates = HardwareEncoderSelector.Enumerate(device.Adapter.Luid);
        try
        {
            foreach (var candidate in candidates)
            {
                if (!candidate.IsHardware && !allowSoftware) continue;
                try
                {
                    return new H264Encoder(device, settings, generation, candidate, onFormat, onSample);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"El codificador {candidate.Name} no se pudo configurar: {ex.Message}");
                }
            }
        }
        finally
        {
            foreach (var candidate in candidates) candidate.Dispose();
        }
        throw new InvalidOperationException("No hay ningún codificador H.264 utilizable.");
    }

    /// <summary>
    /// True si el codificador ya no está leyendo <paramref name="slot"/> (se puede sobrescribir).
    /// Como no hay B-frames, el fotograma n sale antes que el n+1: basta comparar secuencias.
    /// </summary>
    public bool IsSlotFree(Nv12Slot slot)
    {
        long last = slot.LastSubmitSequence;
        if (last < 0) return true;
        lock (_pendingLock)
        {
            if (_pending is { } pending && ReferenceEquals(pending.Slot, slot)) return false;
        }
        // Respaldo por antigüedad por si el codificador descartó algún fotograma internamente.
        return last < Interlocked.Read(ref _completed) || Interlocked.Read(ref _submitted) - last > 12;
    }

    /// <summary>
    /// Entrega un fotograma. Si el codificador está ocupado, se queda como pendiente y sustituye al anterior
    /// pendiente (se descarta el más viejo: menor latencia y nunca se acumula cola). No llama al MFT:
    /// solo el hilo del codificador lo hace, para evitar bloqueos dentro de los drivers.
    /// </summary>
    public void Submit(Nv12Slot slot, long pts)
    {
        if (_stopping) return;
        lock (_pendingLock)
        {
            if (_pending is not null) Interlocked.Increment(ref _replacedPending);
            _pending = (slot, pts);
        }
        _wake.Set();
    }

    /// <summary>Pide que el próximo fotograma sea clave (p. ej. para que una grabación empiece ya).</summary>
    public void RequestKeyFrame() => _keyFrameRequested = true;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping = true;
        _wake.Set();
        // El propio hilo drena el MFT (entrega los fotogramas que tenga dentro) y termina.
        if (!_thread.Join(TimeSpan.FromSeconds(3)))
        {
            Logger.Warn("El hilo del codificador no terminó a tiempo (¿dispositivo perdido?).");
            return; // no se libera el MFT mientras otro hilo podría estar usándolo
        }

        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
            using var shutdown = _transform.QueryInterfaceOrNull<IMFShutdown>();
            shutdown?.Shutdown();
        }
        catch (Exception) { /* el MFT ya estaba caído */ }

        _events?.Dispose();
        _codecApi?.Dispose();
        _transform.Dispose();
        _staging?.Dispose();
        _wake.Dispose();
    }

    private void ConfigureCodecApi()
    {
        if (_codecApi is null)
        {
            Logger.Warn("El codificador no expone ICodecAPI; se usan solo los atributos del tipo de medio.");
            return;
        }
        uint bitrate = (uint)_settings.BitrateBitsPerSecond;
        _codecApi.SetUInt32(CodecApiGuids.RateControlMode, CodecApiGuids.RateControlCbr, "RateControlMode=CBR");
        _codecApi.SetUInt32(CodecApiGuids.MeanBitRate, bitrate, "MeanBitRate");
        _codecApi.SetUInt32(CodecApiGuids.MaxBitRate, bitrate, "MaxBitRate");
        _codecApi.SetUInt32(CodecApiGuids.GopSize, (uint)_settings.FramesPerSecond, "GOPSize");
        _codecApi.SetUInt32(CodecApiGuids.BPictureCount, 0, "BPictureCount");
        _codecApi.SetBool(CodecApiGuids.LowLatencyMode, true, "LowLatencyMode");
        // 0 = más rápido, 100 = mejor calidad. Priorizamos consumo mínimo.
        _codecApi.SetUInt32(CodecApiGuids.QualityVsSpeed, 33, "QualityVsSpeed");
    }

    private void SetOutputType()
    {
        using var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        type.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)_settings.BitrateBitsPerSecond);
        MediaFactory.MFSetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, (uint)_settings.Width, (uint)_settings.Height);
        MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.FrameRate, (uint)_settings.FramesPerSecond, 1);
        MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u /* MFVideoInterlace_Progressive */);
        type.Set(MediaTypeAttributeKeys.Mpeg2Profile, CodecApiGuids.H264ProfileHigh);
        type.Set(CodecApiGuids.MaxKeyframeSpacing, (uint)_settings.FramesPerSecond);
        SetColorAttributes(type);
        _transform.SetOutputType(0, type, 0);
    }

    private void SetInputType()
    {
        using (var type = MediaFactory.MFCreateMediaType())
        {
            FillInputType(type);
            try
            {
                _transform.SetInputType(0, type, 0);
                return;
            }
            catch (SharpGenException)
            {
                // Algunos MFT solo aceptan sus propios tipos: se busca NV12 entre los disponibles.
            }
        }

        for (int i = 0; ; i++)
        {
            IMFMediaType available;
            try { available = _transform.GetInputAvailableType(0, i); }
            catch (SharpGenException) { break; }
            using (available)
            {
                if (available.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12) continue;
                FillInputType(available);
                _transform.SetInputType(0, available, 0);
                return;
            }
        }
        throw new InvalidOperationException("El codificador no acepta NV12.");
    }

    private void FillInputType(IMFMediaType type)
    {
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        MediaFactory.MFSetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, (uint)_settings.Width, (uint)_settings.Height);
        MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.FrameRate, (uint)_settings.FramesPerSecond, 1);
        MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
        SetColorAttributes(type);
    }

    private static void SetColorAttributes(IMFMediaType type)
    {
        // BT.709, rango limitado (coincide con la salida del VideoProcessor).
        type.Set(MediaTypeAttributeKeys.YuvMatrix, 1u /* MFVideoTransferMatrix_BT709 */);
        type.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u /* MFNominalRange_16_235 */);
        type.Set(MediaTypeAttributeKeys.VideoPrimaries, 2u /* MFVideoPrimaries_BT709 */);
        type.Set(MediaTypeAttributeKeys.TransferFunction, 5u /* MFVideoTransFunc_709 */);
    }

    // ---------------------------------------------------------------- hilo del codificador

    /// <summary>
    /// Único hilo que toca el MFT (algunos drivers se bloquean si ProcessInput se llama desde otro hilo
    /// mientras se esperan eventos). En modo asíncrono consulta los eventos sin bloquear
    /// (MF_EVENT_FLAG_NO_WAIT) y duerme hasta que llega un fotograma o pasan unos milisegundos.
    /// </summary>
    private void EncoderLoop()
    {
        bool drainSent = false;
        var drainDeadline = DateTime.MaxValue;
        try
        {
            while (true)
            {
                if (_stopping && !drainSent)
                {
                    lock (_pendingLock) _pending = null;
                    _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
                    _transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
                    drainSent = true;
                    drainDeadline = DateTime.UtcNow.AddSeconds(2);
                    if (!IsAsync)
                    {
                        while (ProcessOneOutputLocked()) { }
                        return;
                    }
                }

                if (IsAsync)
                {
                    if (DrainEvents()) return; // METransformDrainComplete
                    if (drainSent && DateTime.UtcNow > drainDeadline) return;
                    if (!drainSent && _needInput > 0 && TryTakePending(out var work))
                    {
                        if (TryProcessInput(work.Slot, work.Pts)) _needInput--;
                        continue; // revisar eventos enseguida
                    }
                }
                else if (!drainSent && TryTakePending(out var work))
                {
                    TryProcessInput(work.Slot, work.Pts);
                    while (ProcessOneOutputLocked()) { }
                    continue;
                }

                _wake.WaitOne(drainSent ? 1 : 4);
            }
        }
        catch (Exception ex) when (!_stopping)
        {
            ReportFailure(ex);
        }
        catch (Exception ex)
        {
            Logger.Info($"Error del codificador durante el cierre (esperable): {ex.Message}");
        }
    }

    /// <summary>Procesa todos los eventos disponibles. Devuelve true al recibir DrainComplete.</summary>
    private unsafe bool DrainEvents()
    {
        // IMFMediaEventGenerator::GetEvent es la entrada 3 de la vtable. Se llama directamente para que
        // "no hay eventos" sea un HRESULT y no una excepción (se consulta cientos de veces por segundo).
        IntPtr generator = _events!.NativePointer;
        var getEvent = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(*(void***)generator + 3));
        while (true)
        {
            IntPtr pointer;
            int hr = getEvent(generator, 1 /* MF_EVENT_FLAG_NO_WAIT */, &pointer);
            if (hr == MfENoEventsAvailable) return false;
            Marshal.ThrowExceptionForHR(hr);
            using var mediaEvent = new IMFMediaEvent(pointer);
            var status = mediaEvent.Status;
            if (status.Failure) throw new COMException("Error del codificador", status.Code);
            switch (mediaEvent.EventType)
            {
                case MediaEventTypes.TransformNeedInput:
                    Interlocked.Increment(ref _needInputEvents);
                    _needInput++;
                    break;
                case MediaEventTypes.TransformHaveOutput:
                    Interlocked.Increment(ref _haveOutputEvents);
                    ProcessOneOutputLocked();
                    break;
                case MediaEventTypes.TransformDrainComplete:
                    return true;
            }
        }
    }

    private bool TryTakePending(out (Nv12Slot Slot, long Pts) work)
    {
        lock (_pendingLock)
        {
            work = _pending ?? default;
            if (_pending is null) return false;
            if (_submitted - Interlocked.Read(ref _completed) >= MaxFramesInFlight) return false;
            _pending = null;
            return true;
        }
    }

    /// <summary>Envía un fotograma. Devuelve false si el MFT no lo aceptó (vuelve a quedar pendiente).</summary>
    private bool TryProcessInput(Nv12Slot slot, long pts)
    {
        try
        {
            ProcessInputLocked(slot, pts);
            return true;
        }
        catch (SharpGenException ex) when (ex.ResultCode.Code == MfENotAccepting)
        {
            lock (_pendingLock) _pending ??= (slot, pts);
            return false;
        }
    }

    // ---------------------------------------------------------------- entrada / salida

    private void ProcessInputLocked(Nv12Slot slot, long pts)
    {
        using var sample = MediaFactory.MFCreateSample();
        using (var buffer = UsesGpuInput ? CreateGpuBuffer(slot) : CreateSystemMemoryBuffer(slot))
        {
            sample.AddBuffer(buffer);
        }
        sample.SampleTime = pts;
        sample.SampleDuration = _frameDuration;

        // Respaldo: si por ritmo variable el último IDR quedó a más de 1,1 s, se fuerza uno ahora.
        bool lateKeyFrame = _lastKeyFramePts != 0
            && pts - _lastKeyFramePts > MediaClock.TicksPerSecond * 11 / 10
            && pts - _lastForcedPts > MediaClock.TicksPerSecond / 2;
        if (_codecApi is not null && (lateKeyFrame || _keyFrameRequested))
        {
            _keyFrameRequested = false;
            _codecApi.SetUInt32(CodecApiGuids.ForceKeyFrame, 1, "ForceKeyFrame");
            _lastForcedPts = pts;
        }

        _transform.ProcessInput(0, sample, 0);
        slot.LastSubmitSequence = _submitted;
        _submitted++;
    }

    private IMFMediaBuffer CreateGpuBuffer(Nv12Slot slot)
    {
        var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(IidTexture2D, slot.Texture, 0, false);
        using var buffer2D = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
        if (buffer2D is not null) buffer.CurrentLength = (int)buffer2D.ContiguousLength;
        return buffer;
    }

    /// <summary>Respaldo por software: copia la textura NV12 a memoria (lo único que sale de la GPU).</summary>
    private unsafe IMFMediaBuffer CreateSystemMemoryBuffer(Nv12Slot slot)
    {
        int width = _settings.Width, height = _settings.Height;
        int size = width * height * 3 / 2;
        var buffer = MediaFactory.MFCreateMemoryBuffer(size);
        lock (_device.ContextLock)
        {
            _staging ??= _device.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
            _device.Context.CopyResource(_staging, slot.Texture);
            var mapped = _device.Context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                buffer.Lock(out IntPtr destination, out _, out _);
                try
                {
                    byte* src = (byte*)mapped.DataPointer;
                    byte* dst = (byte*)destination;
                    int rows = height * 3 / 2; // plano Y + plano UV intercalado
                    for (int row = 0; row < rows; row++)
                    {
                        System.Buffer.MemoryCopy(src + (long)row * mapped.RowPitch, dst + (long)row * width, width, width);
                    }
                }
                finally
                {
                    buffer.Unlock();
                }
            }
            finally
            {
                _device.Context.Unmap(_staging, 0);
            }
        }
        buffer.CurrentLength = size;
        return buffer;
    }

    /// <summary>Saca una muestra codificada. Devuelve false si el MFT necesita más entrada.</summary>
    private bool ProcessOneOutputLocked()
    {
        var streamInfo = _transform.GetOutputStreamInfo(0);
        const int providesSamples = 0x100 | 0x200; // MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | CAN_PROVIDE_SAMPLES
        IMFSample? ownSample = null;
        if ((streamInfo.Flags & providesSamples) == 0)
        {
            ownSample = MediaFactory.MFCreateSample();
            using var outBuffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(streamInfo.Size, _settings.BitrateBitsPerSecond / 8));
            ownSample.AddBuffer(outBuffer);
        }

        var output = new OutputDataBuffer { StreamID = 0, Sample = ownSample! };
        var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
        output.Events?.Dispose();
        try
        {
            if (result.Code == MfETransformNeedMoreInput) return false;
            if (result.Code == MfETransformStreamChange)
            {
                // El codificador cambió su tipo de salida (p. ej. añadió SPS/PPS): se acepta el nuevo.
                using var newType = _transform.GetOutputAvailableType(0, 0);
                _transform.SetOutputType(0, newType, 0);
                _formatPublished = false;
                return true;
            }
            result.CheckError();
            if (output.Sample is null) return true;
            HandleEncodedSample(output.Sample);
            Interlocked.Increment(ref _completed);
            return true;
        }
        finally
        {
            if (output.Sample is not null && !ReferenceEquals(output.Sample, ownSample)) output.Sample.Dispose();
            ownSample?.Dispose();
        }
    }

    private unsafe void HandleEncodedSample(IMFSample sample)
    {
        long pts = sample.SampleTime;
        long duration = sample.SampleDuration;
        if (duration <= 0) duration = _frameDuration;
        bool cleanPoint = sample.GetUInt32(SampleAttributeKeys.CleanPoint, out uint clean).Success && clean != 0;

        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out IntPtr pointer, out _, out int length);
        try
        {
            var data = new ReadOnlySpan<byte>((void*)pointer, length);
            bool isKeyFrame = cleanPoint || H264Bitstream.ContainsIdr(data);
            if (!_formatPublished)
            {
                if (!isKeyFrame) return; // nada sirve hasta el primer IDR
                PublishFormat(data);
            }
            if (isKeyFrame) _lastKeyFramePts = pts;
            _onSample(new EncodedSampleInfo(StreamKind.Video, pts, duration, isKeyFrame, _generation), data);
        }
        finally
        {
            buffer.Unlock();
        }
    }

    private void PublishFormat(ReadOnlySpan<byte> firstKeyFrame)
    {
        using var type = _transform.GetOutputCurrentType(0);
        if (type.GetBlobSize(MediaTypeAttributeKeys.MpegSequenceHeader, out uint size).Failure || size == 0)
        {
            var header = H264Bitstream.ExtractSequenceHeader(firstKeyFrame);
            if (header is not null) type.SetBlob(MediaTypeAttributeKeys.MpegSequenceHeader, header);
            else Logger.Warn("El primer IDR no trae SPS/PPS; el MP4 dependerá de los que vienen en banda.");
        }
        var format = new StreamFormat(StreamKind.Video, _generation, MediaTypeSerializer.Serialize(type),
            $"H.264 {_settings.Width}x{_settings.Height} {_settings.FramesPerSecond} fps {_settings.BitrateBitsPerSecond / 1_000_000} Mbps ({Name})");
        _formatPublished = true;
        _onFormat(format);
    }

    private void ReportFailure(Exception ex)
    {
        if (_stopping) return;
        _stopping = true;
        Logger.Error($"El codificador {Name} falló", ex);
        Failed?.Invoke(ex);
    }
}
