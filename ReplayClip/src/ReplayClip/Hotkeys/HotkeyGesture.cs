using System.Windows.Input;

namespace ReplayClip.Hotkeys;

/// <summary>
/// Combinación de teclas para un atajo global: modificadores + una tecla. Se guarda como texto
/// legible ("Ctrl+Shift+F9") en los ajustes.
/// </summary>
public readonly record struct HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    // Modificadores de RegisterHotKey.
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    /// <summary>Modificadores en el formato de RegisterHotKey.</summary>
    public uint NativeModifiers =>
        (Modifiers.HasFlag(ModifierKeys.Alt) ? ModAlt : 0)
        | (Modifiers.HasFlag(ModifierKeys.Control) ? ModControl : 0)
        | (Modifiers.HasFlag(ModifierKeys.Shift) ? ModShift : 0)
        | (Modifiers.HasFlag(ModifierKeys.Windows) ? ModWin : 0);

    /// <summary>Código de tecla virtual (VK_*).</summary>
    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    /// <summary>
    /// Un atajo válido necesita al menos un modificador (salvo teclas F13-F24), para no robar teclas
    /// normales al juego.
    /// </summary>
    public bool IsValid =>
        Key != Key.None && !IsModifierKey(Key)
        && (Modifiers != ModifierKeys.None || (Key >= Key.F13 && Key <= Key.F24));

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        // Las teclas numéricas se llaman D0..D9 en WPF; se muestran como "0".."9".
        parts.Add(Key is >= Key.D0 and <= Key.D9 ? ((int)(Key - Key.D0)).ToString() : Key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var modifiers = ModifierKeys.None;
        Key key = Key.None;
        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModifierKeys.Control; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "win" or "windows": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (key != Key.None) return false;
                    if (raw.Length == 1 && char.IsAsciiDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
                    else if (!Enum.TryParse(raw, ignoreCase: true, out key) || int.TryParse(raw, out _)) return false;
                    break;
            }
        }
        gesture = new HotkeyGesture(modifiers, key);
        return gesture.IsValid;
    }

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift
        or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System;
}
