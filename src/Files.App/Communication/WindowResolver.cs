using Microsoft.Extensions.Logging;
using System;

namespace Files.App.Communication
{
    /// <summary>
    /// Default implementation that returns the main window ID.
    /// </summary>
    public sealed class WindowResolver : IWindowResolver
    {
        private readonly ILogger<WindowResolver> _logger;

        public WindowResolver(ILogger<WindowResolver> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public uint GetActiveWindowId()
        {
            // For now, return a default window ID
            // In a real implementation, this would track the actual active window
            // via AppWindow.GetFromWindowId or similar Windows API
            const uint defaultWindowId = 1;
            _logger.LogDebug("Returning default window ID: {WindowId}", defaultWindowId);
            return defaultWindowId;
        }
    }
}