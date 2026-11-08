using ReplayClip.Capture;
using ReplayClip.Core;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ReplayClip.Video;

/// <summary>Textura NV12 del pool que se entrega al codificador.</summary>
public sealed class Nv12Slot : IDisposable
{
    internal Nv12Slot(int index, ID3D11Texture2D texture, ID3D11VideoProcessorOutputView view)
    {
        Index = index;
        Texture = texture;
        OutputView = view;
    }

    public int Index { get; }

    public ID3D11Texture2D Texture { get; }

    internal ID3D11VideoProcessorOutputView OutputView { get; set; }

    /// <summary>Número de secuencia del último envío al codificador (-1 = nunca enviada).</summary>
    public long LastSubmitSequence { get; set; } = -1;

    public void Dispose()
    {
        OutputView.Dispose();
        Texture.Dispose();
    }
}

/// <summary>
/// Convierte BGRA (lo que entrega la captura) a NV12 (lo que piden todos los codificadores por hardware)
/// y escala a la resolución de salida fija con bandas negras, todo en la GPU con ID3D11VideoProcessor.
/// Mantiene un pequeño pool de texturas NV12 y nunca sobrescribe una que el codificador aún esté leyendo.
/// </summary>
public sealed class VideoFrameConverter : IDisposable
{
    private const int PoolSize = 6;

    private readonly D3DDeviceManager _device;
    private readonly List<Nv12Slot> _slots = new(PoolSize);
    private readonly Dictionary<IntPtr, ID3D11VideoProcessorInputView> _inputViews = new();
    private readonly ID3D11Texture2D _blackTexture;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private int _inputWidth, _inputHeight;
    private int _nextSlot;

    public VideoFrameConverter(D3DDeviceManager device, int outputWidth, int outputHeight)
    {
        _device = device;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        _blackTexture = CreateBlackTexture();
        // El pool se crea con un procesador provisional del mismo tamaño de salida.
        EnsureProcessor(outputWidth, outputHeight);
        for (int i = 0; i < PoolSize; i++) _slots.Add(CreateSlot(i));
    }

    public int OutputWidth { get; }

    public int OutputHeight { get; }

    /// <summary>Última textura convertida (la que se repite si no llegan fotogramas nuevos).</summary>
    public Nv12Slot? Latest { get; private set; }

    /// <summary>
    /// Convierte <paramref name="source"/> en una textura NV12 libre. <paramref name="isFree"/> indica si el
    /// codificador ya terminó con una textura. Devuelve null si todas están ocupadas (se descarta el fotograma).
    /// </summary>
    public Nv12Slot? Convert(ID3D11Texture2D source, int contentWidth, int contentHeight, Func<Nv12Slot, bool> isFree)
    {
        lock (_device.ContextLock)
        {
            var slot = AcquireFreeSlot(isFree);
            if (slot is null) return null;

            var description = source.Description;
            EnsureProcessor((int)description.Width, (int)description.Height);
            var inputView = GetInputView(source);
            var processor = _processor!;
            var video = _device.VideoContext;

            // Origen: solo el área con contenido. Destino: centrado conservando la proporción.
            var sourceRect = new RawRect(0, 0, contentWidth, contentHeight);
            var destRect = ComputeLetterbox(contentWidth, contentHeight, OutputWidth, OutputHeight);
            video.VideoProcessorSetStreamSourceRect(processor, 0, true, sourceRect);
            video.VideoProcessorSetStreamDestRect(processor, 0, true, destRect);
            video.VideoProcessorSetOutputTargetRect(processor, true, new RawRect(0, 0, OutputWidth, OutputHeight));

            var streams = new[]
            {
                new VideoProcessorStream { Enable = true, InputSurface = inputView },
            };
            var result = video.VideoProcessorBlt(processor, slot.OutputView, 0, 1, streams);
            if (result.Failure)
            {
                Logger.Warn($"VideoProcessorBlt falló (0x{result.Code:X8}).");
                return null;
            }
            Latest = slot;
            return slot;
        }
    }

    /// <summary>Pone la imagen en negro (objetivo cerrado o todavía inexistente).</summary>
    public Nv12Slot? ShowBlack(Func<Nv12Slot, bool> isFree)
    {
        lock (_device.ContextLock)
        {
            var slot = AcquireFreeSlot(isFree);
            if (slot is null) return null;
            _device.Context.CopyResource(slot.Texture, _blackTexture);
            Latest = slot;
            return slot;
        }
    }

    /// <summary>Olvida las vistas de entrada (al cambiar de objetivo de captura).</summary>
    public void ResetInputCache()
    {
        lock (_device.ContextLock) ClearInputViews();
    }

    public void Dispose()
    {
        lock (_device.ContextLock)
        {
            ClearInputViews();
            foreach (var slot in _slots) slot.Dispose();
            _slots.Clear();
            _processor?.Dispose();
            _enumerator?.Dispose();
            _blackTexture.Dispose();
        }
    }

    /// <summary>Rectángulo centrado que conserva la proporción del origen (bandas negras si hace falta).</summary>
    public static RawRect ComputeLetterbox(int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        double scale = Math.Min(dstWidth / (double)srcWidth, dstHeight / (double)srcHeight);
        int width = Math.Min(dstWidth, (int)Math.Round(srcWidth * scale) & ~1);
        int height = Math.Min(dstHeight, (int)Math.Round(srcHeight * scale) & ~1);
        int left = ((dstWidth - width) / 2) & ~1;
        int top = ((dstHeight - height) / 2) & ~1;
        return new RawRect(left, top, left + width, top + height);
    }

    private Nv12Slot? AcquireFreeSlot(Func<Nv12Slot, bool> isFree)
    {
        for (int attempt = 0; attempt < _slots.Count; attempt++)
        {
            var slot = _slots[(_nextSlot + attempt) % _slots.Count];
            if (ReferenceEquals(slot, Latest) || !isFree(slot)) continue;
            _nextSlot = (slot.Index + 1) % _slots.Count;
            return slot;
        }
        return null;
    }

    private void EnsureProcessor(int inputWidth, int inputHeight)
    {
        if (_processor is not null && inputWidth == _inputWidth && inputHeight == _inputHeight) return;

        ClearInputViews();
        _processor?.Dispose();
        _enumerator?.Dispose();

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)inputWidth,
            InputHeight = (uint)inputHeight,
            OutputWidth = (uint)OutputWidth,
            OutputHeight = (uint)OutputHeight,
            InputFrameRate = new Rational(60, 1),
            OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.OptimalSpeed,
        };
        _enumerator = _device.VideoDevice.CreateVideoProcessorEnumerator(content);
        _processor = _device.VideoDevice.CreateVideoProcessor(_enumerator, 0);
        _inputWidth = inputWidth;
        _inputHeight = inputHeight;

        // Las vistas de salida van ligadas al enumerador: se rehacen (las texturas no cambian).
        foreach (var slot in _slots)
        {
            slot.OutputView.Dispose();
            slot.OutputView = CreateOutputView(slot.Texture);
        }

        var video = _device.VideoContext;
        video.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        video.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        video.VideoProcessorSetOutputBackgroundColor(_processor, false, new VideoColor { Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 } });
        ConfigureColorSpaces(video);
    }

    private void ConfigureColorSpaces(ID3D11VideoContext video)
    {
        // Entrada sRGB rango completo → salida BT.709 rango limitado (lo que esperan los reproductores para HD).
        try
        {
            using var video1 = video.QueryInterfaceOrNull<ID3D11VideoContext1>();
            if (video1 is null) return;
            video1.VideoProcessorSetStreamColorSpace1(_processor!, 0, ColorSpaceType.RgbFullG22NoneP709);
            video1.VideoProcessorSetOutputColorSpace1(_processor!, ColorSpaceType.YcbcrStudioG22LeftP709);
        }
        catch (Exception ex)
        {
            Logger.Warn($"No se pudo fijar el espacio de color BT.709: {ex.Message}");
        }
    }

    private ID3D11VideoProcessorInputView GetInputView(ID3D11Texture2D texture)
    {
        if (_inputViews.TryGetValue(texture.NativePointer, out var view)) return view;
        // El pool de captura tiene 2 texturas; si hay muchas vistas es que el pool se recreó.
        if (_inputViews.Count >= 4) ClearInputViews();
        var description = new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        };
        view = _device.VideoDevice.CreateVideoProcessorInputView(texture, _enumerator!, description);
        _inputViews[texture.NativePointer] = view;
        return view;
    }

    private void ClearInputViews()
    {
        foreach (var view in _inputViews.Values) view.Dispose();
        _inputViews.Clear();
    }

    private Nv12Slot CreateSlot(int index)
    {
        var description = new Texture2DDescription
        {
            Width = (uint)OutputWidth,
            Height = (uint)OutputHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        ID3D11Texture2D texture;
        try
        {
            texture = _device.Device.CreateTexture2D(description);
        }
        catch (Exception)
        {
            description.BindFlags = BindFlags.RenderTarget;
            texture = _device.Device.CreateTexture2D(description);
        }
        return new Nv12Slot(index, texture, CreateOutputView(texture));
    }

    private ID3D11VideoProcessorOutputView CreateOutputView(ID3D11Texture2D texture)
    {
        var viewDescription = new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        };
        return _device.VideoDevice.CreateVideoProcessorOutputView(texture, _enumerator!, viewDescription);
    }

    private unsafe ID3D11Texture2D CreateBlackTexture()
    {
        // NV12 negro en rango limitado: Y = 16, U = V = 128.
        int lumaBytes = OutputWidth * OutputHeight;
        var data = new byte[lumaBytes * 3 / 2];
        data.AsSpan(0, lumaBytes).Fill(16);
        data.AsSpan(lumaBytes).Fill(128);
        var description = new Texture2DDescription
        {
            Width = (uint)OutputWidth,
            Height = (uint)OutputHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        };
        fixed (byte* pointer = data)
        {
            var initial = new SubresourceData(pointer, (uint)OutputWidth);
            try
            {
                return _device.Device.CreateTexture2D(description, initial);
            }
            catch (Exception)
            {
                description.BindFlags = BindFlags.RenderTarget;
                return _device.Device.CreateTexture2D(description, initial);
            }
        }
    }
}
