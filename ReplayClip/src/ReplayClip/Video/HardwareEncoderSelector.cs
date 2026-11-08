using System.Runtime.InteropServices;
using ReplayClip.Core;
using Vortice.MediaFoundation;

namespace ReplayClip.Video;

/// <summary>Codificador H.264 disponible en el sistema.</summary>
public sealed class EncoderCandidate : IDisposable
{
    internal EncoderCandidate(IMFActivate activate, string name, bool isHardware)
    {
        Activate = activate;
        Name = name;
        IsHardware = isHardware;
    }

    public IMFActivate Activate { get; }

    /// <summary>Nombre del MFT, p. ej. "Intel® Quick Sync Video H.264 Encoder MFT" o "NVIDIA H.264 Encoder MFT".</summary>
    public string Name { get; }

    public bool IsHardware { get; }

    public void Dispose() => Activate.Dispose();
}

/// <summary>
/// Enumera los codificadores H.264 de Media Foundation. Primero los de hardware de la GPU indicada
/// (MFTEnum2 + MFT_ENUM_ADAPTER_LUID: Quick Sync, NVENC o AMF según el fabricante), y al final el
/// codificador por software de Microsoft como respaldo.
/// </summary>
public static class HardwareEncoderSelector
{
    public static List<EncoderCandidate> Enumerate(long adapterLuid)
    {
        var result = new List<EncoderCandidate>();
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };

        try
        {
            using var attributes = MediaFactory.MFCreateAttributes(1);
            attributes.Set(CodecApiGuids.MftEnumAdapterLuid, (ulong)adapterLuid);
            uint flags = (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter);
            MediaFactory.MFTEnum2(TransformCategoryGuids.VideoEncoder, flags, input, output, attributes, out IntPtr array, out uint count);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var activate = new IMFActivate(Marshal.ReadIntPtr(array, i * IntPtr.Size));
                    result.Add(new EncoderCandidate(activate, FriendlyName(activate), true));
                }
            }
            finally
            {
                if (array != IntPtr.Zero) Marshal.FreeCoTaskMem(array);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"No se pudieron enumerar codificadores de hardware: {ex.Message}");
        }

        try
        {
            uint flags = (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter);
            using var collection = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, input, output);
            foreach (var item in collection)
            {
                // Referencia propia: la colección libera las suyas al desecharse.
                Marshal.AddRef(item.NativePointer);
                var activate = new IMFActivate(item.NativePointer);
                result.Add(new EncoderCandidate(activate, FriendlyName(activate), false));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"No se pudo enumerar el codificador por software: {ex.Message}");
        }

        foreach (var candidate in result)
        {
            Logger.Info($"Codificador encontrado: {candidate.Name} ({(candidate.IsHardware ? "hardware" : "software")})");
        }
        return result;
    }

    private static string FriendlyName(IMFActivate activate)
    {
        try { return activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute); }
        catch (Exception) { return "Codificador H.264"; }
    }
}
