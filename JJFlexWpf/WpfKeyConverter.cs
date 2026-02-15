using System.Windows.Input;
using WinFormsKeys = System.Windows.Forms.Keys;

namespace JJFlexWpf;

/// <summary>
/// Converts WPF Key/ModifierKeys to WinForms Keys enum values.
/// Required for routing WPF PreviewKeyDown events to the existing
/// VB.NET KeyCommands.DoCommand(Keys) infrastructure.
///
/// Sprint 8 Phase 8.6.
/// </summary>
public static class WpfKeyConverter
{
    /// <summary>
    /// Convert WPF KeyEventArgs to a WinForms Keys value.
    /// Combines the key with modifier flags (Control, Alt, Shift).
    /// </summary>
    public static WinFormsKeys ToWinFormsKeys(KeyEventArgs e)
    {
        // Get the actual key (resolve System keys like Alt+letter)
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        var result = MapKey(key);

        // Add modifier flags
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            result |= WinFormsKeys.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            result |= WinFormsKeys.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            result |= WinFormsKeys.Shift;

        return result;
    }

    /// <summary>
    /// Map a WPF Key to the equivalent WinForms Keys value.
    /// Covers all keys used in KeyCommands.defaultKeys.
    /// </summary>
    private static WinFormsKeys MapKey(Key key)
    {
        return key switch
        {
            // Function keys
            Key.F1 => WinFormsKeys.F1,
            Key.F2 => WinFormsKeys.F2,
            Key.F3 => WinFormsKeys.F3,
            Key.F4 => WinFormsKeys.F4,
            Key.F5 => WinFormsKeys.F5,
            Key.F6 => WinFormsKeys.F6,
            Key.F7 => WinFormsKeys.F7,
            Key.F8 => WinFormsKeys.F8,
            Key.F9 => WinFormsKeys.F9,
            Key.F10 => WinFormsKeys.F10,
            Key.F11 => WinFormsKeys.F11,
            Key.F12 => WinFormsKeys.F12,
            Key.F13 => WinFormsKeys.F13,
            Key.F14 => WinFormsKeys.F14,
            Key.F15 => WinFormsKeys.F15,
            Key.F16 => WinFormsKeys.F16,
            Key.F17 => WinFormsKeys.F17,
            Key.F18 => WinFormsKeys.F18,
            Key.F19 => WinFormsKeys.F19,
            Key.F20 => WinFormsKeys.F20,
            Key.F21 => WinFormsKeys.F21,
            Key.F22 => WinFormsKeys.F22,
            Key.F23 => WinFormsKeys.F23,
            Key.F24 => WinFormsKeys.F24,

            // Letters (A-Z)
            Key.A => WinFormsKeys.A,
            Key.B => WinFormsKeys.B,
            Key.C => WinFormsKeys.C,
            Key.D => WinFormsKeys.D,
            Key.E => WinFormsKeys.E,
            Key.F => WinFormsKeys.F,
            Key.G => WinFormsKeys.G,
            Key.H => WinFormsKeys.H,
            Key.I => WinFormsKeys.I,
            Key.J => WinFormsKeys.J,
            Key.K => WinFormsKeys.K,
            Key.L => WinFormsKeys.L,
            Key.M => WinFormsKeys.M,
            Key.N => WinFormsKeys.N,
            Key.O => WinFormsKeys.O,
            Key.P => WinFormsKeys.P,
            Key.Q => WinFormsKeys.Q,
            Key.R => WinFormsKeys.R,
            Key.S => WinFormsKeys.S,
            Key.T => WinFormsKeys.T,
            Key.U => WinFormsKeys.U,
            Key.V => WinFormsKeys.V,
            Key.W => WinFormsKeys.W,
            Key.X => WinFormsKeys.X,
            Key.Y => WinFormsKeys.Y,
            Key.Z => WinFormsKeys.Z,

            // Numbers (0-9)
            Key.D0 => WinFormsKeys.D0,
            Key.D1 => WinFormsKeys.D1,
            Key.D2 => WinFormsKeys.D2,
            Key.D3 => WinFormsKeys.D3,
            Key.D4 => WinFormsKeys.D4,
            Key.D5 => WinFormsKeys.D5,
            Key.D6 => WinFormsKeys.D6,
            Key.D7 => WinFormsKeys.D7,
            Key.D8 => WinFormsKeys.D8,
            Key.D9 => WinFormsKeys.D9,

            // Navigation
            Key.Escape => WinFormsKeys.Escape,
            Key.Return => WinFormsKeys.Return,
            Key.Tab => WinFormsKeys.Tab,
            Key.Space => WinFormsKeys.Space,
            Key.Back => WinFormsKeys.Back,
            Key.Delete => WinFormsKeys.Delete,
            Key.Insert => WinFormsKeys.Insert,
            Key.Home => WinFormsKeys.Home,
            Key.End => WinFormsKeys.End,
            Key.PageUp => WinFormsKeys.PageUp,
            Key.PageDown => WinFormsKeys.PageDown,
            Key.Up => WinFormsKeys.Up,
            Key.Down => WinFormsKeys.Down,
            Key.Left => WinFormsKeys.Left,
            Key.Right => WinFormsKeys.Right,

            // Punctuation and special
            Key.OemPeriod => WinFormsKeys.OemPeriod,
            Key.OemComma => WinFormsKeys.Oemcomma,
            Key.OemMinus => WinFormsKeys.OemMinus,
            Key.OemPlus => WinFormsKeys.Oemplus,
            Key.OemQuestion => WinFormsKeys.OemQuestion,   // / ? (same as Oem2)
            Key.Oem1 => WinFormsKeys.Oem1,            // ; :
            Key.Oem3 => WinFormsKeys.Oem3,            // ` ~
            Key.Oem4 => WinFormsKeys.Oem4,            // [ {
            Key.Oem5 => WinFormsKeys.Oem5,            // \ |
            Key.Oem6 => WinFormsKeys.Oem6,            // ] }
            Key.Oem7 => WinFormsKeys.Oem7,            // ' "

            // Numpad
            Key.NumPad0 => WinFormsKeys.NumPad0,
            Key.NumPad1 => WinFormsKeys.NumPad1,
            Key.NumPad2 => WinFormsKeys.NumPad2,
            Key.NumPad3 => WinFormsKeys.NumPad3,
            Key.NumPad4 => WinFormsKeys.NumPad4,
            Key.NumPad5 => WinFormsKeys.NumPad5,
            Key.NumPad6 => WinFormsKeys.NumPad6,
            Key.NumPad7 => WinFormsKeys.NumPad7,
            Key.NumPad8 => WinFormsKeys.NumPad8,
            Key.NumPad9 => WinFormsKeys.NumPad9,
            Key.Multiply => WinFormsKeys.Multiply,
            Key.Add => WinFormsKeys.Add,
            Key.Subtract => WinFormsKeys.Subtract,
            Key.Decimal => WinFormsKeys.Decimal,
            Key.Divide => WinFormsKeys.Divide,

            // Default: return None for unmapped keys
            _ => WinFormsKeys.None
        };
    }
}
