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
            return Convert.ToBase64String(buf)
                .TrimEnd('=')
                .Replace('+','-')
                .Replace('/','_');
        }
    }
}
