using System;
using System.Threading.Tasks;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage;

namespace Files.App.Communication
{
    // DPAPI-backed token store. Stores encrypted token in LocalSettings and maintains an epoch for rotation.
    internal static class ProtectedTokenStore
    {
        private const string KeyToken = "Files_RemoteControl_ProtectedToken";
        private const string KeyEnabled = "Files_RemoteControl_Enabled";
        private const string KeyEpoch = "Files_RemoteControl_TokenEpoch";
        private static ApplicationDataContainer Settings => ApplicationData.Current.LocalSettings;

        public static async Task SetTokenAsync(string token)
        {
            var provider = new DataProtectionProvider("LOCAL=user");
            var buffer = CryptographicBuffer.ConvertStringToBinary(token, BinaryStringEncoding.Utf8);
            var protectedBuf = await provider.ProtectAsync(buffer);
            var bytes = CryptographicBuffer.EncodeToBase64String(protectedBuf);
            Settings.Values[KeyToken] = bytes;
        }

        public static bool IsEnabled()
        {
            if (Settings.Values.TryGetValue(KeyEnabled, out var v) && v is bool b) return b;
            return false;
        }

        public static void SetEnabled(bool enabled) => Settings.Values[KeyEnabled] = enabled;

        public static async Task<string> GetOrCreateTokenAsync()
        {
            if (Settings.Values.TryGetValue(KeyToken, out var val) && val is string b64 && !string.IsNullOrEmpty(b64))
            {
                try
                {
                    var protectedBuf = CryptographicBuffer.DecodeFromBase64String(b64);
                    var provider = new DataProtectionProvider();
                    var unprotected = await provider.UnprotectAsync(protectedBuf);
                    return CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, unprotected);
                }
                catch
                {
                    // fallback to regen
                }
            }

            var t = Guid.NewGuid().ToString("N");
            await SetTokenAsync(t);
            SetEpoch(1);
            return t;
        }

        public static int GetEpoch()
        {
            if (Settings.Values.TryGetValue(KeyEpoch, out var v) && v is int e) return e;
            SetEpoch(1);
            return 1;
        }

        private static void SetEpoch(int epoch) => Settings.Values[KeyEpoch] = epoch;

        public static async Task<string> RotateTokenAsync()
        {
            var t = Guid.NewGuid().ToString("N");
            await SetTokenAsync(t);
            var epoch = GetEpoch() + 1;
            SetEpoch(epoch);
            return t;
        }
    }
}