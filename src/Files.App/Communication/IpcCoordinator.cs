using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Files.App.ViewModels;
using Files.App.Data.Contracts;
using System.Text.RegularExpressions;

namespace Files.App.Communication
{
    /// <summary>
    /// Routes IPC requests to appropriate shell adapters. No UI code.
    /// </summary>
    public sealed partial class IpcCoordinator
    {
        // Source-generated regex patterns for stack trace sanitization (compile-time optimization for .NET 7+)
        [GeneratedRegex(@"[A-Z]:\\[^:]+\.cs:line \d+", RegexOptions.IgnoreCase)]
        private static partial Regex FilePathRegex();
        
        [GeneratedRegex(@"[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}")]
        private static partial Regex GuidRegex();
        
        [GeneratedRegex(@"(?<![A-Za-z0-9+/=_-])[A-Za-z0-9_\-/+]{20,}={0,2}(?![A-Za-z0-9+/=_-])")]
        private static partial Regex Base64TokenRegex();
        
        [GeneratedRegex(@"\s+")]
        private static partial Regex WhitespaceRegex();
        
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
            var stack = ex.StackTrace ?? string.Empty;
            try
            {
                // Use source-generated regex patterns for better performance
                // Remove absolute Windows file paths ending with .cs:line N
                stack = FilePathRegex().Replace(stack, string.Empty);
                // Remove GUIDs
                stack = GuidRegex().Replace(stack, string.Empty);
                // Remove likely base64 tokens (length > 20, url safe or standard base64 charset)
                stack = Base64TokenRegex().Replace(stack, "[redacted]");
                // Collapse whitespace
                stack = WhitespaceRegex().Replace(stack, " ");
                // Keep it reasonably small
                if (stack.Length > Constants.IpcSettings.StackTraceSanitizationMaxLength) 
                    stack = stack[..Constants.IpcSettings.StackTraceSanitizationMaxLength] + "...";
            }
            catch { stack = string.Empty; }
            return ex.GetType().Name + ": " + ex.Message + (string.IsNullOrEmpty(stack) ? string.Empty : "; stack: " + stack);
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
                            JsonRpcMessage.MakeError(request.Id, JsonRpcException.InternalError, 
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
                    await _comm.SendResponseAsync(client, JsonRpcMessage.MakeError(request.Id, JsonRpcException.InternalError, sanitized));
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
                _logger.LogError(ex, "Failed to list shells");
                throw new JsonRpcException(JsonRpcException.InternalError, "Failed to enumerate shells");
            }
        }

        private (string Title, bool IsFocused, object Bounds) GetWindowInfo(uint appWindowId)
        {
            try
            {
                // For now, return basic info. We can enhance this later with actual window API calls
                return (
                    Title: "Files", // Could get actual window title via Win32 API
                    IsFocused: appWindowId == _windows.GetActiveWindowId(),
                    Bounds: new { x = 0, y = 0, width = 1920, height = 1080 } // Placeholder
                );
            }
            catch
            {
                return ("Unknown", false, new { x = 0, y = 0, width = 0, height = 0 });
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
                    throw new JsonRpcException(JsonRpcException.MethodNotFound, 
                        $"Method '{request.Method}' not implemented");
            }
        }
    }
}