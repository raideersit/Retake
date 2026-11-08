using System.Runtime.InteropServices;
using ReplayClip.Core;

namespace ReplayClip.Video;

/// <summary>
/// Acceso mínimo a ICodecAPI (codecapi.h), que Vortice no expone. Se llama por la vtable con punteros
/// a función: no es una dependencia nueva, solo interop COM estándar de .NET.
/// </summary>
internal sealed unsafe class CodecApi : IDisposable
{
    private static readonly Guid IidCodecApi = new("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA");

    private IntPtr _pointer;

    private CodecApi(IntPtr pointer) => _pointer = pointer;

    /// <summary>Obtiene ICodecAPI del MFT, o null si el codificador no lo implementa.</summary>
    public static CodecApi? From(IntPtr transform)
    {
        Guid iid = IidCodecApi;
        return Marshal.QueryInterface(transform, ref iid, out IntPtr pointer) >= 0 ? new CodecApi(pointer) : null;
    }

    public bool IsSupported(Guid api)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, int>)VTable(3);
        return fn(_pointer, &api) == 0;
    }

    /// <summary>Fija un valor UINT32. Devuelve false (y lo registra) si el codificador no lo acepta.</summary>
    public bool SetUInt32(Guid api, uint value, string name)
    {
        var variant = new PropVariant { VarType = 19 /* VT_UI4 */, UInt32Value = value };
        return Set(api, ref variant, name, value.ToString());
    }

    public bool SetBool(Guid api, bool value, string name)
    {
        var variant = new PropVariant { VarType = 11 /* VT_BOOL */, BoolValue = (short)(value ? -1 : 0) };
        return Set(api, ref variant, name, value.ToString());
    }

    public void Dispose()
    {
        if (_pointer != IntPtr.Zero)
        {
            Marshal.Release(_pointer);
            _pointer = IntPtr.Zero;
        }
    }

    private bool Set(Guid api, ref PropVariant value, string name, string text)
    {
        // ICodecAPI::SetValue es la entrada 9 de la vtable.
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, PropVariant*, int>)VTable(9);
        int hr;
        fixed (PropVariant* pointer = &value) hr = fn(_pointer, &api, pointer);
        if (hr < 0)
        {
            Logger.Info($"El codificador no aceptó {name} = {text} (0x{hr:X8}).");
            return false;
        }
        return true;
    }

    private IntPtr VTable(int slot) => (IntPtr)(*(*(void***)_pointer + slot));

    /// <summary>VARIANT de 24 bytes (x64) con los únicos tipos que usamos.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public uint UInt32Value;
        [FieldOffset(8)] public short BoolValue;
    }
}

/// <summary>GUIDs de codecapi.h y mfapi.h que no trae Vortice.</summary>
internal static class CodecApiGuids
{
    public static readonly Guid RateControlMode = new("1C0608E9-370C-4710-8A58-CB6181C42423");
    public static readonly Guid MeanBitRate = new("F7222374-2144-4815-B550-A37F8E12EE52");
    public static readonly Guid MaxBitRate = new("9651EAE4-39B9-4EBF-85EF-D7F444EC7465");
    public static readonly Guid GopSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");
    public static readonly Guid BPictureCount = new("8D390AAC-DC5C-4200-B57F-814D04BABAB2");
    public static readonly Guid LowLatencyMode = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");
    public static readonly Guid ForceKeyFrame = new("398C1B98-8353-475A-9EF2-8F265D260345");
    public static readonly Guid QualityVsSpeed = new("98332DF8-03CD-476B-89FA-3F9E442DEC9F");

    /// <summary>MFT_ENUM_ADAPTER_LUID: filtra los codificadores de una GPU concreta en MFTEnum2.</summary>
    public static readonly Guid MftEnumAdapterLuid = new("1D39518C-E220-4DA8-A07F-BA172552D6B1");

    /// <summary>MF_MT_MAX_KEYFRAME_SPACING: respaldo del tamaño de GOP en el tipo de medio.</summary>
    public static readonly Guid MaxKeyframeSpacing = new("C16EB52B-73A1-476F-8D62-839D6A020652");

    /// <summary>MF_MT_MPEG2_PROFILE con eAVEncH264VProfile_High = 100.</summary>
    public const uint H264ProfileHigh = 100;

    /// <summary>eAVEncCommonRateControlMode_CBR.</summary>
    public const uint RateControlCbr = 0;
}
