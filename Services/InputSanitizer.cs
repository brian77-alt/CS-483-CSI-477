using System.Text.RegularExpressions;

namespace CS_483_CSI_477.Services
{
    public static class InputSanitizer
    {
        // Max lengths
        private const int MaxChatMessage = 1000;
        private const int MaxSearchInput = 100;
        private const int MaxGeneralInput = 255;

        public static string SanitizeChat(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            input = input.Trim();
            if (input.Length > MaxChatMessage) input = input[..MaxChatMessage];
            // Remove HTML tags
            input = Regex.Replace(input, @"<[^>]*>", "");
            // Remove null bytes
            input = input.Replace("\0", "");
            return input;
        }

        public static string SanitizeSearch(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            input = input.Trim();
            if (input.Length > MaxSearchInput) input = input[..MaxSearchInput];
            input = Regex.Replace(input, @"[<>""'%;()&+]", "");
            return input;
        }

        public static string SanitizeGeneral(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            input = input.Trim();
            if (input.Length > MaxGeneralInput) input = input[..MaxGeneralInput];
            input = Regex.Replace(input, @"<[^>]*>", "");
            input = input.Replace("\0", "");
            return input;
        }

        public static bool IsValidEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;
            return Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
        }

        public static bool IsValidCourseCode(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            return Regex.IsMatch(code.Trim(), @"^[A-Z]{2,5}\s*\d{3}$", RegexOptions.IgnoreCase);
        }

        public static string SanitizeSql(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            return input.Replace("'", "''").Replace("\\", "\\\\").Replace("\0", "");
        }
    }
}