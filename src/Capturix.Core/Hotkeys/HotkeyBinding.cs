using Capturix.Core.Interop;

namespace Capturix.Core.Hotkeys;

/// <summary>A global hotkey such as "Ctrl+Shift+A", parsed into RegisterHotKey arguments.</summary>
public readonly record struct HotkeyBinding(uint Modifiers, Keys Key)
{
    public static bool TryParse(string text, out HotkeyBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        uint modifiers = 0;
        Keys key = Keys.None;
        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= Native.MOD_CONTROL;
                    break;
                case "shift":
                    modifiers |= Native.MOD_SHIFT;
                    break;
                case "alt":
                    modifiers |= Native.MOD_ALT;
                    break;
                case "win":
                    modifiers |= Native.MOD_WIN;
                    break;
                case "prtsc":
                case "printscreen":
                    key = Keys.PrintScreen;
                    break;
                default:
                    if (!Enum.TryParse(raw, ignoreCase: true, out key))
                        return false;
                    break;
            }
        }

        if (key == Keys.None)
            return false;
        binding = new HotkeyBinding(modifiers, key);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((Modifiers & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((Modifiers & Native.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }
}
