using System.Windows.Input;
using AudioPilot.Models;

namespace AudioPilot.Services.Hotkeys;

internal sealed class HotkeyParsingService
{
    public static (HotkeyMainInput mainInput, List<Key> modifiers)? ParseHotkeyString(string hotkeyString)
    {
        var parsed = HotkeySyntax.Parse(hotkeyString);
        if (!parsed.HasValue)
        {
            return null;
        }

        List<Key> modifiers = [.. parsed.Value.Modifiers.Select(static modifier => (Key)modifier)];
        return (HotkeyMainInput.FromSyntax(parsed.Value.MainInput), modifiers);
    }

    public static string BuildComboKey(IEnumerable<Key> modifiers, HotkeyMainInput mainInput) =>
        HotkeySyntax.BuildComboKey(modifiers.Select(static k => (HotkeyKey)k), mainInput.ToSyntax());

    public static void ClearCache() => HotkeySyntax.ClearCache();

    /// <summary>Validates a shortcut, preserving display order unless canonical modifier order is requested.</summary>
    internal static bool TryNormalizeHotkey(string? rawHotkey, out string normalized, bool canonicalModifierOrder = false) =>
        HotkeySyntax.TryNormalize(rawHotkey, out normalized, canonicalModifierOrder);
}
