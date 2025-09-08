using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Files.App.ViewModels;
using Files.App.Data.Contracts;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Text;

namespace Files.App.Communication
{
    /// <summary>
    /// Routes IPC requests to appropriate shell adapters. No UI code.
    /// </summary>
    public sealed partial class IpcCoordinator
    {
        // Standard error codes for consistent error handling
        private const int ERROR_NO_SHELL = -32001;  // No shell available to handle request
        private const int ERROR_DISPATCH = -32002;  // Failed to dispatch to adapter
        private const int ERROR_SHELL_LIST = -32003; // Failed to enumerate shells
        
        // Source-generated regex patterns for stack trace sanitization (compile-time optimization for .NET 7+)
        // Windows absolute paths (C:\path\file.cs:line N)
        [GeneratedRegex(@"[A-Z]:\\[^:""<>|]+\.cs:line \d+", RegexOptions.IgnoreCase)]
        private static partial Regex WindowsPathRegex();
        
        // Unix-style paths (/path/file.cs:line N)
        [GeneratedRegex(@"/[^:""<>|]+\.cs:line \d+")]
        private static partial Regex UnixPathRegex();
        
        // UNC network paths (\\server\share\file.cs:line N)
        [GeneratedRegex(@"\\\\[^\\:""<>|]+\\[^:""<>|]+\.cs:line \d+", RegexOptions.IgnoreCase)]
        private static partial Regex UncPathRegex();
        
        // Matches GUIDs in standard format
        [GeneratedRegex(@"\b[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}\b")]
        private static partial Regex GuidRegex();
        
        // Matches potential base64 tokens (JWT segments, API keys, etc.) - more specific pattern
        // Looks for base64-like strings that are 20+ chars, bounded by non-base64 chars
        [GeneratedRegex(@"\b(?:ey[A-Za-z0-9_-]{18,}|[A-Za-z0-9+/]{32,}={0,2}|[A-Za-z0-9_-]{32,})\b")]
        private static partial Regex Base64TokenRegex();
        
        // Matches method signatures with namespaces for sanitization
        [GeneratedRegex(@"\bat [A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+\.")]
        private static partial Regex MethodSignatureRegex();
        
        [GeneratedRegex(@"\s+")]
        private static partial Regex WhitespaceRegex();
        
        // Win32 API for window bounds retrieval
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
        
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        
        private readonly IIpcShellRegistry _registry;
        private readonly IAppCommunicationService _comm;
        private readonly IWindowResolver _windows;
        private readonly ILogger<IpcCoordinator> _logger;

        public IpcCoordinator(
            IIpcShellRegistry registry,
            IAppCommunicationService comm,
            IWindowResolver windows,
            ILogger<IpcCoordinator> logger)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _comm = comm ?? throw new ArgumentNullException(nameof(comm));
            _windows = windows ?? throw new ArgumentNullException(nameof(windows));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Initialize()
        {
            _comm.OnRequestReceived += HandleRequestAsync;
            _logger.LogInformation("IPC coordinator initialized - routing enabled");
        }

        private static string SanitizeException(Exception ex)
        {
            // Sanitize the exception message first to remove any sensitive data
            var sanitizedMessage = SanitizeMessage(ex.Message);
            
            // Early return for exceptions we don't want to expose stack traces for
            var sensitiveExceptions = new[] { "UnauthorizedAccessException", "SecurityException", "CryptographicException" };
            if (sensitiveExceptions.Contains(ex.GetType().Name))
            {
                return $"{ex.GetType().Name}: Access denied";
            }
            
            var stack = ex.StackTrace ?? string.Empty;
            if (string.IsNullOrWhiteSpace(stack))
            {
                return $"{ex.GetType().Name}: {sanitizedMessage}";
            }
            
            try
            {
                // Remove all file paths (Windows, Linux, Mac, UNC) - these reveal directory structure
                stack = WindowsPathRegex().Replace(stack, "[path]:[line]");
                stack = UnixPathRegex().Replace(stack, "[path]:[line]");
                stack = UncPathRegex().Replace(stack, "[path]:[line]");
                
                // Remove GUIDs which might be correlation IDs or sensitive identifiers
                stack = GuidRegex().Replace(stack, "[guid]");
                
                // Remove potential tokens (JWTs start with 'ey', API keys, base64 strings)
                stack = Base64TokenRegex().Replace(stack, "[token]");
                
                // Sanitize method signatures to remove full namespace paths
                stack = MethodSignatureRegex().Replace(stack, "at [namespace].");
                
                // Remove any remaining absolute paths that might have been missed
                stack = Regex.Replace(stack, @"[A-Z]:\\[^\s]+", "[path]", RegexOptions.IgnoreCase);
                stack = Regex.Replace(stack, @"/(?:home|usr|var|opt|Users|Applications)/[^\s]+", "[path]");
                stack = Regex.Replace(stack, @"\\\\[^\\\s]+\\[^\s]+", "[unc-path]");
                
                // Remove port numbers which might reveal internal infrastructure
                stack = Regex.Replace(stack, @":\d{2,5}\b", ":[port]");
                
                // Remove IP addresses
                stack = Regex.Replace(stack, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "[ip]");
                
                // Collapse excessive whitespace
                stack = WhitespaceRegex().Replace(stack, " ").Trim();
                
                // Truncate if too long, but keep it useful for debugging
                if (stack.Length > Constants.IpcSettings.StackTraceSanitizationMaxLength)
                {
                    // Try to keep the most relevant part (usually the top of the stack)
                    // Cut at word boundary for cleaner truncation
                    var maxLength = Constants.IpcSettings.StackTraceSanitizationMaxLength;
                    var lastSpace = stack.LastIndexOf(' ', maxLength - 1);
                    var cutPoint = lastSpace > maxLength * 0.8 ? lastSpace : maxLength; // Use word boundary if not too far back
                    stack = stack[..cutPoint].TrimEnd() + "... [truncated]";
                }
            }
            catch
            {
                // If sanitization fails, don't expose the original stack
                stack = "[sanitization failed]";
            }
            
            return $"{ex.GetType().Name}: {sanitizedMessage}" + 
                   (string.IsNullOrWhiteSpace(stack) ? string.Empty : $" | Stack: {stack}");
        }
        
        private static string SanitizeMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "[no message]";
            
            // Remove file paths from messages
            message = Regex.Replace(message, @"[A-Z]:\\[^\s""]+", "[path]", RegexOptions.IgnoreCase);
            message = Regex.Replace(message, @"/(?:home|usr|var|opt|Users|Applications)/[^\s""]+", "[path]");
            
            // Remove potential tokens from error messages
            message = Regex.Replace(message, @"\b[A-Za-z0-9+/=_-]{32,}\b", "[token]");
            
            // Remove GUIDs
            message = Regex.Replace(message, @"\b[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}\b", "[guid]");
            
            // Limit message length
            if (message.Length > 200)
                message = message[..197] + "...";
            
            return message;
        }

        private async Task HandleRequestAsync(ClientContext client, JsonRpcMessage request)
        {
            var startTime = DateTime.UtcNow;
            
            try
            {
                _logger.LogDebug("IPC request: {Method} from {ClientId}", request.Method, client.Id);

                // Resolve target shell
                var targetShell = ResolveShell(request);
                if (targetShell == null)
                {
                    var shellCount = _registry.List().Count;
                    _logger.LogWarning("No shell available for request {Method}. Total registered shells: {Count}", 
                        request.Method, shellCount);
                    if (!request.IsNotification)
                    {
                        await _comm.SendResponseAsync(client, 
                            JsonRpcMessage.MakeError(request.Id, ERROR_NO_SHELL, 
                                $"No shell available to handle request. Registered shells: {shellCount}"));
                    }
                    return;
                }

                _logger.LogDebug("Routing {Method} to shell {ShellId}", request.Method, targetShell.ShellId);

                // Dispatch to adapter (adapter handles UI marshaling)
                var result = await DispatchToAdapterAsync(targetShell.Adapter, request);
                
                if (!request.IsNotification)
                {
                    var safeResult = result ?? new { status = "ok" };
                    await _comm.SendResponseAsync(client, JsonRpcMessage.MakeResult(request.Id, safeResult));
                }

                var elapsed = DateTime.UtcNow - startTime;
                _logger.LogDebug("IPC request {Method} completed in {ElapsedMs}ms", 
                    request.Method, elapsed.TotalMilliseconds);
            }
            catch (JsonRpcException jre)
            {
                _logger.LogWarning("JSON-RPC error {Code} for {Method}: {Message}", 
                    jre.Code, request.Method, jre.Message);
                
                if (!request.IsNotification)
                {
                    await _comm.SendResponseAsync(client, 
                        JsonRpcMessage.MakeError(request.Id, jre.Code, jre.Message));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error handling IPC request {Method}", request.Method);
                
                if (!request.IsNotification)
                {
                    var sanitized = SanitizeException(ex);
                    
                    // Use specific error code if it's a dispatch failure
                    var errorCode = ex is InvalidOperationException ? ERROR_DISPATCH : JsonRpcException.InternalError;
                    
                    await _comm.SendResponseAsync(client, JsonRpcMessage.MakeError(request.Id, errorCode, sanitized));
                }
            }
        }

        private IpcShellDescriptor? ResolveShell(JsonRpcMessage request)
        {
            try
            {
                // 1. Check for explicit targetShellId in params
                if (request.Params.HasValue && request.Params.Value.TryGetProperty("targetShellId", out var shellIdElem) &&
                    shellIdElem.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(shellIdElem.GetString(), out var shellId))
                {
                    var shell = _registry.GetById(shellId);
                    if (shell != null)
                    {
                        _logger.LogDebug("Resolved shell by explicit ID: {ShellId}", shellId);
                        return shell;
                    }
                }

                // 2. Check for explicit windowId in params
                if (request.Params.HasValue && request.Params.Value.TryGetProperty("windowId", out var windowIdElem) &&
                    windowIdElem.TryGetUInt32(out var windowId))
                {
                    var shell = _registry.GetActiveForWindow(windowId);
                    if (shell != null)
                    {
                        _logger.LogDebug("Resolved shell by window ID: {WindowId}", windowId);
                        return shell;
                    }
                }

                // 3. Fallback: use active shell in active window
                var activeWindowId = _windows.GetActiveWindowId();
                var activeShell = _registry.GetActiveForWindow(activeWindowId);
                
                if (activeShell != null)
                {
                    _logger.LogDebug("Resolved shell from active window {WindowId}", activeWindowId);
                    return activeShell;
                }

                // 4. Last resort: any available shell
                var anyShell = _registry.List().FirstOrDefault();
                if (anyShell != null)
                {
                    _logger.LogDebug("Using any available shell: {ShellId}", anyShell.ShellId);
                    return anyShell;
                }

                _logger.LogWarning("No shells available in registry");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving shell for request");
                return null;
            }
        }

        private async Task<object> ListShellsAsync()
        {
            try
            {
                var allShells = _registry.List();
                var shellInfos = new List<object>();

                foreach (var shell in allShells)
                {
                    try
                    {
                        // Get shell state from the adapter
                        dynamic state = await shell.Adapter.GetStateAsync();
                        dynamic actions = await shell.Adapter.ListActionsAsync();

                        // Get window information
                        var windowInfo = GetWindowInfo(shell.AppWindowId);
                        
                        var shellInfo = new
                        {
                            // Shell identifiers
                            shellId = shell.ShellId.ToString(),
                            windowId = shell.AppWindowId,
                            tabId = shell.TabId.ToString(),
                            isActive = shell.IsActive,
                            
                            // Window information  
                            window = new
                            {
                                pid = System.Diagnostics.Process.GetCurrentProcess().Id, // Current process
                                title = windowInfo.Title,
                                isFocused = windowInfo.IsFocused,
                                bounds = windowInfo.Bounds
                            },
                            
                            // Shell state information
                            currentPath = (string)(state?.currentPath ?? "Unknown"),
                            canNavigateBack = (bool)(state?.canNavigateBack ?? false),
                            canNavigateForward = (bool)(state?.canNavigateForward ?? false),
                            isLoading = (bool)(state?.isLoading ?? false),
                            itemCount = (int)(state?.itemCount ?? 0),
                            
                            // Available actions for this shell
                            availableActions = actions?.actions ?? new object[0]
                        };
                        
                        shellInfos.Add(shellInfo);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to get info for shell {ShellId}", shell.ShellId);
                        // Include shell even if we can't get full info
                        shellInfos.Add(new
                        {
                            shellId = shell.ShellId.ToString(),
                            windowId = shell.AppWindowId,
                            tabId = shell.TabId.ToString(),
                            isActive = shell.IsActive,
                            error = "Failed to retrieve shell information"
                        });
                    }
                }

                return new { shells = shellInfos };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list shells: {ExceptionType}", ex.GetType().Name);
                throw new JsonRpcException(ERROR_SHELL_LIST, $"Failed to enumerate shells: {ex.GetType().Name}");
            }
        }

        private (string Title, bool IsFocused, object Bounds) GetWindowInfo(uint appWindowId)
        {
            try
            {
                // Get the actual window handle from MainWindow
                var hWnd = MainWindow.Instance?.WindowHandle ?? IntPtr.Zero;
                if (hWnd == IntPtr.Zero)
                {
                    return (
                        Title: "Files",
                        IsFocused: false,
                        Bounds: new { x = 0, y = 0, width = 0, height = 0, error = "No window handle" }
                    );
                }

                // Get actual window title using Win32 API
                var titleBuilder = new StringBuilder(256);
                GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
                var title = titleBuilder.ToString();
                if (string.IsNullOrEmpty(title))
                    title = "Files";

                // Get actual window bounds using Win32 API
                if (!GetWindowRect(hWnd, out RECT rect))
                {
                    return (
                        Title: title,
                        IsFocused: appWindowId == _windows.GetActiveWindowId(),
                        Bounds: new { x = 0, y = 0, width = 0, height = 0, error = "GetWindowRect failed" }
                    );
                }

                // Check if this window is currently focused
                var foregroundWindow = GetForegroundWindow();
                var isFocused = (hWnd == foregroundWindow) || (appWindowId == _windows.GetActiveWindowId());

                return (
                    Title: title,
                    IsFocused: isFocused,
                    Bounds: new 
                    { 
                        x = rect.Left, 
                        y = rect.Top, 
                        width = rect.Right - rect.Left, 
                        height = rect.Bottom - rect.Top 
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get window info for window ID {WindowId}", appWindowId);
                return ("Files", false, new { x = 0, y = 0, width = 0, height = 0, error = ex.Message });
            }
        }

        private async Task<object> ExecuteActionOnTargetShellAsync(string actionId, string? targetShellId, ShellIpcAdapter fallbackAdapter)
        {
            ShellIpcAdapter targetAdapter = fallbackAdapter;
            
            // If a specific shell is requested, find it
            if (!string.IsNullOrEmpty(targetShellId) && Guid.TryParse(targetShellId, out var shellGuid))
            {
                var targetShell = _registry.GetById(shellGuid);
                if (targetShell == null)
                {
                    _logger.LogWarning("Target shell {ShellId} not found", targetShellId);
                    throw new JsonRpcException(JsonRpcException.InvalidParams, $"Shell '{targetShellId}' not found");
                }
                targetAdapter = targetShell.Adapter;
                _logger.LogInformation("Executing action '{ActionId}' on targeted shell {ShellId}", actionId, targetShellId);
            }
            else
            {
                _logger.LogInformation("Executing action '{ActionId}' on default shell", actionId);
            }
            
            return await targetAdapter.ExecuteActionAsync(actionId);
        }

        private async Task<object?> DispatchToAdapterAsync(ShellIpcAdapter adapter, JsonRpcMessage request)
        {
            // Call the adapter's public methods directly
            
            switch (request.Method)
            {
                case "getState":
                    return await adapter.GetStateAsync();

                case "listActions":
                    return await adapter.ListActionsAsync();

                case "listShells":
                    return await ListShellsAsync();

                case "navigate":
                    if (request.Params.HasValue && request.Params.Value.TryGetProperty("path", out var pathProp))
                    {
                        var path = pathProp.GetString();
                        if (string.IsNullOrWhiteSpace(path))
                            throw new JsonRpcException(JsonRpcException.InvalidParams, "Missing path parameter");
                        return await adapter.NavigateAsync(path);
                    }
                    throw new JsonRpcException(JsonRpcException.InvalidParams, "Missing path parameter");

                case "getMetadata":
                    var paths = new List<string>();
                    if (request.Params.HasValue && request.Params.Value.TryGetProperty("paths", out var pathsElem) && 
                        pathsElem.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in pathsElem.EnumerateArray())
                        {
                            if (p.ValueKind == JsonValueKind.String)
                            {
                                var s = p.GetString();
                                if (!string.IsNullOrWhiteSpace(s))
                                    paths.Add(s);
                            }
                        }
                    }
                    return await adapter.GetMetadataAsync(paths);

                case "executeAction":
                    if (request.Params.HasValue && request.Params.Value.TryGetProperty("actionId", out var actionIdProp))
                    {
                        var actionId = actionIdProp.GetString();
                        if (string.IsNullOrWhiteSpace(actionId))
                            throw new JsonRpcException(JsonRpcException.InvalidParams, "Missing actionId parameter");
                            
                        // Check if a specific shell is targeted
                        string? targetShellId = null;
                        if (request.Params.Value.TryGetProperty("targetShellId", out var shellIdProp))
                        {
                            targetShellId = shellIdProp.GetString();
                        }
                        
                        return await ExecuteActionOnTargetShellAsync(actionId, targetShellId, adapter);
                    }
                    throw new JsonRpcException(JsonRpcException.InvalidParams, "Missing actionId parameter");

                default:
                    _logger.LogWarning("Unknown IPC method requested: {Method}", request.Method);
                    throw new JsonRpcException(JsonRpcException.MethodNotFound, 
                        $"Method '{request.Method}' not implemented");
            }
        }
    }
}