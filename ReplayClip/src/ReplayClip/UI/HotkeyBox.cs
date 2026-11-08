using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ReplayClip.Hotkeys;

namespace ReplayClip.UI;

/// <summary>Cuadro de texto que captura una combinación de teclas en lugar de texto.</summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty GestureTextProperty = DependencyProperty.Register(
        nameof(GestureText), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((HotkeyBox)d).Text = (string)e.NewValue));

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        ToolTip = "Haz clic y pulsa la combinación (p. ej. Ctrl+Shift+F9)";
    }

    /// <summary>Combinación en texto ("Ctrl+Shift+F9").</summary>
    public string GestureText
    {
        get => (string)GetValue(GestureTextProperty);
        set => SetValue(GestureTextProperty, value);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Escape or Key.Tab) return;
        if (HotkeyGesture.IsModifierKey(key)) return; // esperar a la tecla principal
        var gesture = new HotkeyGesture(Keyboard.Modifiers, key);
        if (!gesture.IsValid)
        {
            Text = "Usa Ctrl, Shift o Alt + una tecla";
            return;
        }
        GestureText = gesture.ToString();
    }
}
