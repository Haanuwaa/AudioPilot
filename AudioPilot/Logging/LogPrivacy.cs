using System.Security.Cryptography;
using System.Text;

namespace AudioPilot.Logging
{
    public static class LogPrivacy
    {
        private static int _redactionEnabled = 1;

        public static bool IsRedactionEnabled => Volatile.Read(ref _redactionEnabled) != 0;

        public static string Label(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "<empty>";
            }

            string trimmed = value.Trim();
            if (!IsRedactionEnabled)
            {
                return trimmed;
            }

            return RedactedLabel(trimmed);
        }

        internal static string RedactedLabel(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "<empty>";
            }

            string trimmed = value.Trim();
            return $"len={trimmed.Length} hash={Hash(trimmed)}";
        }

        public static string Device(string? name) => $"device[{Label(name)}]";
        public static string Process(string? name) => $"process[{Label(name)}]";
        public static string Session(string? displayName) => $"session[{Label(displayName)}]";
        public static string Id(string? id) => $"id[{Label(id)}]";

        public static void ApplySettings(Models.Settings? settings)
        {
            bool redact = settings?.Miscellaneous.RedactLogContent ?? true;
            Volatile.Write(ref _redactionEnabled, redact ? 1 : 0);
        }

        private static string Hash(string value)
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);
            Span<byte> utf8 = byteCount <= 512 ? stackalloc byte[byteCount] : new byte[byteCount];
            Encoding.UTF8.GetBytes(value, utf8);
            Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(utf8, hash);
            return Convert.ToHexString(hash[..4]);
        }
    }
}
