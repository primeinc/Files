namespace Files.App.Communication
{
	// Runtime configuration for IPC system - uses constants from Constants.IpcSettings as defaults
	public static class IpcConfig
	{
		public static long WebSocketMaxMessageBytes { get; set; } = Constants.IpcSettings.WebSocketMaxMessageBytes;

		public static long NamedPipeMaxMessageBytes { get; set; } = Constants.IpcSettings.NamedPipeMaxMessageBytes;

		public static long PerClientQueueCapBytes { get; set; } = Constants.IpcSettings.PerClientQueueCapBytes;

		public static int RateLimitPerSecond { get; set; } = Constants.IpcSettings.RateLimitPerSecond;

		public static int RateLimitBurst { get; set; } = Constants.IpcSettings.RateLimitBurst;

		public static int SelectionNotificationCap { get; set; } = Constants.IpcSettings.SelectionNotificationCap;

		public static int GetMetadataMaxItems { get; set; } = Constants.IpcSettings.GetMetadataMaxItems;

		public static int GetMetadataTimeoutSec { get; set; } = Constants.IpcSettings.GetMetadataTimeoutSec;

		public static bool EnableIpcInDebugMode { get; set; } = false; // opt-in
		
		public static int SendLoopPollingIntervalMs { get; set; } = 10; // milliseconds between queue checks
	}
}