using System.Text;

namespace AudioPilot.Services.UI
{
    /// <summary>
    /// Uses Unicode 17.0 Emoji and Emoji_Presentation properties instead of whole symbol blocks.
    /// Source: https://www.unicode.org/Public/17.0.0/ucd/emoji/emoji-data.txt.
    /// Unicode's copyright and permission notice is included in THIRD-PARTY-NOTICES.txt.
    /// Joiners and selectors alone do not turn ordinary writing into emoji.
    /// </summary>
    internal static class OverlayEmojiClassifier
    {
        public static bool MightContainEmoji(string text)
        {
            foreach (Rune rune in text.EnumerateRunes())
            {
                if (rune.Value == 0x20E3 || rune.Value >= 0xA9 && (HasDefaultPresentation(rune.Value) || HasTextPresentation(rune.Value)))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsEmoji(string textElement)
        {
            bool emojiPresentation = false;
            int previous = -1;
            foreach (Rune rune in textElement.EnumerateRunes())
            {
                int value = rune.Value;
                if (previous == -1 && (value < 0xA9 || !HasDefaultPresentation(value) && !HasTextPresentation(value)))
                {
                    return IsKeycap(textElement);
                }

                if (value == 0xFE0E)
                {
                    return false;
                }

                if (HasDefaultPresentation(value)
                    || value == 0xFE0F && previous >= 0xA9 && HasTextPresentation(previous))
                {
                    emojiPresentation = true;
                }

                previous = value;
            }

            return emojiPresentation || IsKeycap(textElement);
        }

        private static bool IsKeycap(string text) =>
            text.Length is 2 or 3
            && text[0] is '#' or '*' or >= '0' and <= '9'
            && text[^1] == '\u20E3'
            && (text.Length == 2 || text[1] == '\uFE0F');

        private static bool HasDefaultPresentation(int value) => value is
            >= 0x231A and <= 0x231B or >= 0x23E9 and <= 0x23EC or 0x23F0 or 0x23F3 or >= 0x25FD and <= 0x25FE or
            >= 0x2614 and <= 0x2615 or >= 0x2648 and <= 0x2653 or 0x267F or 0x2693 or 0x26A1 or >= 0x26AA and <= 0x26AB or
            >= 0x26BD and <= 0x26BE or >= 0x26C4 and <= 0x26C5 or 0x26CE or 0x26D4 or 0x26EA or >= 0x26F2 and <= 0x26F3 or
            0x26F5 or 0x26FA or 0x26FD or 0x2705 or >= 0x270A and <= 0x270B or 0x2728 or 0x274C or 0x274E or
            >= 0x2753 and <= 0x2755 or 0x2757 or >= 0x2795 and <= 0x2797 or 0x27B0 or 0x27BF or >= 0x2B1B and <= 0x2B1C or
            0x2B50 or 0x2B55 or 0x1F004 or 0x1F0CF or 0x1F18E or >= 0x1F191 and <= 0x1F19A or >= 0x1F1E6 and <= 0x1F1FF or
            0x1F201 or 0x1F21A or 0x1F22F or >= 0x1F232 and <= 0x1F236 or >= 0x1F238 and <= 0x1F23A or
            >= 0x1F250 and <= 0x1F251 or >= 0x1F300 and <= 0x1F320 or >= 0x1F32D and <= 0x1F335 or
            >= 0x1F337 and <= 0x1F37C or >= 0x1F37E and <= 0x1F393 or >= 0x1F3A0 and <= 0x1F3CA or
            >= 0x1F3CF and <= 0x1F3D3 or >= 0x1F3E0 and <= 0x1F3F0 or 0x1F3F4 or >= 0x1F3F8 and <= 0x1F43E or 0x1F440 or
            >= 0x1F442 and <= 0x1F4FC or >= 0x1F4FF and <= 0x1F53D or >= 0x1F54B and <= 0x1F54E or
            >= 0x1F550 and <= 0x1F567 or 0x1F57A or >= 0x1F595 and <= 0x1F596 or 0x1F5A4 or >= 0x1F5FB and <= 0x1F64F or
            >= 0x1F680 and <= 0x1F6C5 or 0x1F6CC or >= 0x1F6D0 and <= 0x1F6D2 or >= 0x1F6D5 and <= 0x1F6D8 or
            >= 0x1F6DC and <= 0x1F6DF or >= 0x1F6EB and <= 0x1F6EC or >= 0x1F6F4 and <= 0x1F6FC or
            >= 0x1F7E0 and <= 0x1F7EB or 0x1F7F0 or >= 0x1F90C and <= 0x1F93A or >= 0x1F93C and <= 0x1F945 or
            >= 0x1F947 and <= 0x1F9FF or >= 0x1FA70 and <= 0x1FA7C or >= 0x1FA80 and <= 0x1FA8A or
            >= 0x1FA8E and <= 0x1FAC6 or 0x1FAC8 or >= 0x1FACD and <= 0x1FADC or >= 0x1FADF and <= 0x1FAEA or
            >= 0x1FAEF and <= 0x1FAF8;

        private static bool HasTextPresentation(int value) => value is
            0x23 or 0x2A or >= 0x30 and <= 0x39 or 0xA9 or 0xAE or 0x203C or 0x2049 or 0x2122 or 0x2139 or
            >= 0x2194 and <= 0x2199 or >= 0x21A9 and <= 0x21AA or 0x2328 or 0x23CF or >= 0x23ED and <= 0x23EF or
            >= 0x23F1 and <= 0x23F2 or >= 0x23F8 and <= 0x23FA or 0x24C2 or >= 0x25AA and <= 0x25AB or 0x25B6 or 0x25C0 or
            >= 0x25FB and <= 0x25FC or >= 0x2600 and <= 0x2604 or 0x260E or 0x2611 or 0x2618 or 0x261D or 0x2620 or
            >= 0x2622 and <= 0x2623 or 0x2626 or 0x262A or >= 0x262E and <= 0x262F or >= 0x2638 and <= 0x263A or 0x2640 or
            0x2642 or >= 0x265F and <= 0x2660 or 0x2663 or >= 0x2665 and <= 0x2666 or 0x2668 or 0x267B or 0x267E or
            0x2692 or >= 0x2694 and <= 0x2697 or 0x2699 or >= 0x269B and <= 0x269C or 0x26A0 or 0x26A7 or
            >= 0x26B0 and <= 0x26B1 or 0x26C8 or 0x26CF or 0x26D1 or 0x26D3 or 0x26E9 or >= 0x26F0 and <= 0x26F1 or
            0x26F4 or >= 0x26F7 and <= 0x26F9 or 0x2702 or >= 0x2708 and <= 0x2709 or >= 0x270C and <= 0x270D or 0x270F or
            0x2712 or 0x2714 or 0x2716 or 0x271D or 0x2721 or >= 0x2733 and <= 0x2734 or 0x2744 or 0x2747 or
            >= 0x2763 and <= 0x2764 or 0x27A1 or >= 0x2934 and <= 0x2935 or >= 0x2B05 and <= 0x2B07 or 0x3030 or 0x303D or
            0x3297 or 0x3299 or >= 0x1F170 and <= 0x1F171 or >= 0x1F17E and <= 0x1F17F or 0x1F202 or 0x1F237 or 0x1F321 or
            >= 0x1F324 and <= 0x1F32C or 0x1F336 or 0x1F37D or >= 0x1F396 and <= 0x1F397 or >= 0x1F399 and <= 0x1F39B or
            >= 0x1F39E and <= 0x1F39F or >= 0x1F3CB and <= 0x1F3CE or >= 0x1F3D4 and <= 0x1F3DF or 0x1F3F3 or 0x1F3F5 or
            0x1F3F7 or 0x1F43F or 0x1F441 or 0x1F4FD or >= 0x1F549 and <= 0x1F54A or >= 0x1F56F and <= 0x1F570 or
            >= 0x1F573 and <= 0x1F579 or 0x1F587 or >= 0x1F58A and <= 0x1F58D or 0x1F590 or 0x1F5A5 or 0x1F5A8 or
            >= 0x1F5B1 and <= 0x1F5B2 or 0x1F5BC or >= 0x1F5C2 and <= 0x1F5C4 or >= 0x1F5D1 and <= 0x1F5D3 or
            >= 0x1F5DC and <= 0x1F5DE or 0x1F5E1 or 0x1F5E3 or 0x1F5E8 or 0x1F5EF or 0x1F5F3 or 0x1F5FA or 0x1F6CB or
            >= 0x1F6CD and <= 0x1F6CF or >= 0x1F6E0 and <= 0x1F6E5 or 0x1F6E9 or 0x1F6F0 or 0x1F6F3;
    }
}
