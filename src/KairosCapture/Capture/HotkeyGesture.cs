using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace KairosCapture.Capture;

internal readonly record struct HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    internal uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);
    internal uint NativeModifiers => 0x4000u | (uint)Modifiers;

    internal static bool TryCreate(ModifierKeys modifiers, Key key, out HotkeyGesture gesture)
    {
        gesture = default;
        bool supportedKey = key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or >= Key.F1 and <= Key.F24;
        if (!supportedKey || (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0 ||
            (modifiers & ~(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows)) != 0) return false;
        gesture = new(modifiers, key);
        return true;
    }

    internal static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        ModifierKeys modifiers = ModifierKeys.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            ModifierKeys modifier = parts[i].ToUpperInvariant() switch
            { "CTRL" or "CONTROL" or "STRG" => ModifierKeys.Control, "ALT" => ModifierKeys.Alt,
                "SHIFT" or "UMSCHALT" => ModifierKeys.Shift, "WIN" or "WINDOWS" => ModifierKeys.Windows, _ => ModifierKeys.None };
            if (modifier == ModifierKeys.None || modifiers.HasFlag(modifier)) return false;
            modifiers |= modifier;
        }
        string last = parts[^1];
        if (last.Length == 1 && char.IsAsciiDigit(last[0])) last = "D" + last;
        return Enum.TryParse(last, true, out Key key) && TryCreate(modifiers, key, out gesture);
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Strg");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Umschalt");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Key is >= Key.D0 and <= Key.D9 ? ((int)Key - (int)Key.D0).ToString(System.Globalization.CultureInfo.InvariantCulture) : Key.ToString());
        return string.Join('+', parts);
    }
}
