using System.Text.Json.Serialization;
using System.Windows.Input;
using HDRSnip.Interop;

namespace HDRSnip.Models;

/// <summary>Win32 <c>MOD_*</c> flags, as passed to <c>RegisterHotKey</c>.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

/// <summary>A global hotkey: a modifier set plus a virtual-key code.</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, uint VirtualKey)
{
    [JsonIgnore]
    public bool IsEmpty => VirtualKey == 0;

    public static Hotkey None => new(HotkeyModifiers.None, 0);

    /// <summary>Builds a hotkey from a live WPF key event, or null if it is not usable.</summary>
    public static Hotkey? FromKeyEvent(Key key, ModifierKeys modifiers)
    {
        // System keys arrive as Key.System with the real key in SystemKey.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin
                or Key.System or Key.None)
            return null;

        var flags = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Alt)) flags |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Control)) flags |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Shift)) flags |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) flags |= HotkeyModifiers.Windows;

        // Print Screen and the function keys are useful on their own; anything
        // else without a modifier would swallow ordinary typing system-wide.
        bool standalone = key is Key.PrintScreen or Key.Pause
                          || (key >= Key.F1 && key <= Key.F24);
        if (flags == HotkeyModifiers.None && !standalone)
            return null;

        return new Hotkey(flags, (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    public override string ToString()
    {
        if (IsEmpty)
            return "Not set";

        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join(" + ", parts);
    }

    private static string KeyName(uint virtualKey) => virtualKey switch
    {
        0x2C => "Print Screen",
        0x0D => "Enter",
        0x20 => "Space",
        0x09 => "Tab",
        0x13 => "Pause",
        >= 0x70 and <= 0x87 => $"F{virtualKey - 0x6F}",
        >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
        >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        _ => MapPrintable(virtualKey)
    };

    private static string MapPrintable(uint virtualKey)
    {
        const uint mapVkToChar = 2;
        uint mapped = Native.MapVirtualKeyW(virtualKey, mapVkToChar) & 0xFFFF;
        return mapped != 0 ? char.ToUpperInvariant((char)mapped).ToString() : $"0x{virtualKey:X2}";
    }
}
