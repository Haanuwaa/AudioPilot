using System.Windows.Input;
using AudioPilot.Models;

namespace AudioPilot.Services.Hotkeys;

internal static class HotkeyReservedShortcutPolicy
{
    public static bool IsSupported(HotkeyMainInput input, IEnumerable<Key> modifiers) =>
        HotkeySyntax.IsSupported(input.ToSyntax(), modifiers.Select(static k => (HotkeyKey)k));

    public static bool IsReserved(HotkeyMainInput input, IEnumerable<Key> modifiers, out string reservedShortcutName) =>
        HotkeySyntax.IsReserved(input.ToSyntax(), modifiers.Select(static k => (HotkeyKey)k), out reservedShortcutName);
}
