using System.Globalization;

namespace ReplayClip.UI;

/// <summary>Formato de tiempos "m:ss.f" y lectura flexible ("83.5", "83,5", "1:23.5").</summary>
public static class TimeText
{
    public static string Format(double seconds)
    {
        seconds = Math.Max(0, seconds);
        int minutes = (int)(seconds / 60);
        double rest = seconds - minutes * 60;
        return $"{minutes}:{rest.ToString("00.0", CultureInfo.InvariantCulture)}";
    }

    public static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim().Replace(',', '.');
        double minutes = 0;
        int colon = text.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!double.TryParse(text[..colon], NumberStyles.Float, CultureInfo.InvariantCulture, out minutes)) return false;
            text = text[(colon + 1)..];
        }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) || s < 0 || minutes < 0) return false;
        seconds = minutes * 60 + s;
        return true;
    }
}
