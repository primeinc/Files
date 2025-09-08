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
                // Get the window ID from the current MainWindow's AppWindow with overflow protection
                // This works because Files is typically single-window (main window)
                // For multi-window support, would need to track the focused window
                uint windowId;
                if (MainWindow.Instance?.AppWindow?.Id.Value is ulong rawId)
                {
                    if (rawId <= uint.MaxValue)
                    {
                        windowId = (uint)rawId;
                    }
                    else
                    {
                        // Log a warning as this could lead to incorrect window identification
                        _logger.LogWarning("Window ID ({RawId}) exceeds uint.MaxValue and will be truncated to 0.", rawId);
                        windowId = 0u;
                    }
                }
                else
                {
                    windowId = 0u;
                }
                
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