using System;
using System.Security.Cryptography;

namespace Files.App.Communication
{
    public static class EphemeralTokenHelper
    {
        public static string GenerateToken(int bytes = 32)
        {
            Span<byte> buf = stackalloc byte[bytes];
            RandomNumberGenerator.Fill(buf);
            return ToUrlSafeBase64(buf);
        }

        // Converts raw bytes to a URL-safe Base64 string without padding per RFC 4648 §5
        private static string ToUrlSafeBase64(ReadOnlySpan<byte> data)
        {
            return Convert.ToBase64String(data)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }
}
