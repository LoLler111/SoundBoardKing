using System.Windows.Input;

namespace SoundBoardKing.App.Hotkeys;

/// <summary>A global key combination, stored as text such as "Ctrl+Alt+1" or "Shift+Num5".</summary>
public readonly record struct HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8;

    public uint NativeModifiers =>
        (Modifiers.HasFlag(ModifierKeys.Alt) ? ModAlt : 0) |
        (Modifiers.HasFlag(ModifierKeys.Control) ? ModControl : 0) |
        (Modifiers.HasFlag(ModifierKeys.Shift) ? ModShift : 0) |
        (Modifiers.HasFlag(ModifierKeys.Windows) ? ModWin : 0);

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        var modifiers = ModifierKeys.None;
        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win": modifiers |= ModifierKeys.Windows; break;
                default: return false;
            }
        }

        if (!TryParseKey(parts[^1], out var key) || IsModifierKey(key))
            return false;
        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }

    public static string FormatModifiers(ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return string.Join("+", parts);
    }

    public override string ToString()
    {
        var modifiers = FormatModifiers(Modifiers);
        var key = FormatKey(Key);
        return modifiers.Length == 0 ? key : $"{modifiers}+{key}";
    }

    private static string FormatKey(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (int)(key - Key.NumPad0),
        _ => key.ToString(),
    };

    private static bool TryParseKey(string text, out Key key)
    {
        if (text.Length == 1 && char.IsAsciiDigit(text[0]))
        {
            key = Key.D0 + (text[0] - '0');
            return true;
        }
        if (text.Length == 4 && text.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(text[3]))
        {
            key = Key.NumPad0 + (text[3] - '0');
            return true;
        }
        return Enum.TryParse(text, ignoreCase: true, out key) && key != Key.None;
    }
}
