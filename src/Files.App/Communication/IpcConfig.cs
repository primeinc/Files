namespace Files.App.Communication
{
    // Centralized runtime caps and config values (tune from Settings UI).
    public static class IpcConfig
    {
        public static int WebSocketMaxMessageBytes { get; set; } = 16 * 1024 * 1024; // 16 MB
        public static int NamedPipeMaxMessageBytes { get; set; } = 10 * 1024 * 1024; // 10 MB
        public static int PerClientQueueCapBytes { get; set; } = 2 * 1024 * 1024; // 2 MB
        public static int RateLimitPerSecond { get; set; } = 20;
        public static int RateLimitBurst { get; set; } = 60;
        public static int SelectionNotificationCap { get; set; } = 200;
        public static int GetMetadataMaxItems { get; set; } = 500;
        public static int GetMetadataTimeoutSec { get; set; } = 30;
    }
}