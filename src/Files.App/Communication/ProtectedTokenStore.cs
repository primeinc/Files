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
		// Static fields
		private const string KEY_TOKEN = "Files_RemoteControl_ProtectedToken";
		private const string KEY_ENABLED = "Files_RemoteControl_Enabled";
		private const string KEY_EPOCH = "Files_RemoteControl_TokenEpoch";

		// Static properties
		private static ApplicationDataContainer Settings => ApplicationData.Current.LocalSettings;

		// Static methods
		public static bool IsEnabled()
		{
			if (Settings.Values.TryGetValue(KEY_ENABLED, out var v) && v is bool b) 
				return b;

			return false;
		}

		public static void SetEnabled(bool enabled) => Settings.Values[KEY_ENABLED] = enabled;

		public static int GetEpoch()
		{
			if (Settings.Values.TryGetValue(KEY_EPOCH, out var v) && v is int e) 
				return e;

			SetEpoch(1);
			return 1;
		}

		public static async Task<string> GetOrCreateTokenAsync()
		{
			if (Settings.Values.TryGetValue(KEY_TOKEN, out var val) && val is string b64 && !string.IsNullOrEmpty(b64))
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

		public static async Task<string> RotateTokenAsync()
		{
			var t = Guid.NewGuid().ToString("N");
			await SetTokenAsync(t);
			var epoch = GetEpoch() + 1;
			SetEpoch(epoch);
			return t;
		}

		private static async Task SetTokenAsync(string token)
		{
			var provider = new DataProtectionProvider("LOCAL=user");
			var buffer = CryptographicBuffer.ConvertStringToBinary(token, BinaryStringEncoding.Utf8);
			var protectedBuf = await provider.ProtectAsync(buffer);
			var bytes = CryptographicBuffer.EncodeToBase64String(protectedBuf);
			Settings.Values[KEY_TOKEN] = bytes;
		}

		private static void SetEpoch(int epoch) => Settings.Values[KEY_EPOCH] = epoch;
	}
}