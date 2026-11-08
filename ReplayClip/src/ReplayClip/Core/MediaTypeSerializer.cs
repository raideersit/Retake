using Vortice.MediaFoundation;

namespace ReplayClip.Core;

/// <summary>Convierte un IMFMediaType en bytes y viceversa (MFGetAttributesAsBlob / MFInitAttributesFromBlob).</summary>
public static class MediaTypeSerializer
{
    public static byte[] Serialize(IMFMediaType type)
    {
        uint size = MediaFactory.MFGetAttributesAsBlobSize(type);
        var blob = new byte[size];
        MediaFactory.MFGetAttributesAsBlob(type, blob, size);
        return blob;
    }

    /// <summary>Crea un tipo de medio nuevo (el llamador lo libera).</summary>
    public static IMFMediaType Deserialize(byte[] blob)
    {
        var type = MediaFactory.MFCreateMediaType();
        MediaFactory.MFInitAttributesFromBlob(type, blob, (uint)blob.Length);
        return type;
    }
}
