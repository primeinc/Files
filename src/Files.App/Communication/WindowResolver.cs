using Microsoft.Extensions.Logging;
using System;
using Files.App.Views;

namespace Files.App.Communication
{
    /// <summary>
    /// Resolves the active window ID for IPC routing.
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
            try
            {
                // Get the window ID from the current MainWindow's AppWindow
                // This works because Files is typically single-window (main window)
                // For multi-window support, would need to track the focused window
                var windowId = MainWindow.Instance?.AppWindow?.Id.Value is ulong id ? (uint)id : 0u;
                
                if (windowId == 0)
                {
                    _logger.LogWarning("Could not determine active window ID, using default");
                    return 1; // Default fallback
                }
                
                _logger.LogDebug("Resolved active window ID: {WindowId}", windowId);
                return windowId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get active window ID");
                return 1; // Default fallback on error
            }
        }
    }
}