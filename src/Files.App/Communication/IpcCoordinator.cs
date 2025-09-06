using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Files.App.ViewModels;
using Files.App.Data.Contracts;

namespace Files.App.Communication
{
    /// <summary>
    /// Routes IPC requests to appropriate shell adapters. No UI code.
    /// </summary>
    public sealed class IpcCoordinator
    {
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
                    await _comm.SendResponseAsync(client, JsonRpcMessage.MakeResult(request.Id, result));
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
                    // Include the actual exception details in the error message for debugging
                    var errorMessage = $"Exception: {ex.GetType().Name}: {ex.Message}. StackTrace: {ex.StackTrace?.Replace("\r\n", " ").Replace("\n", " ").Substring(0, Math.Min(ex.StackTrace?.Length ?? 0, 500))}";
                    await _comm.SendResponseAsync(client, 
                        JsonRpcMessage.MakeError(request.Id, JsonRpcException.InternalError, errorMessage));
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

        private async Task<object?> DispatchToAdapterAsync(ShellIpcAdapter adapter, JsonRpcMessage request)
        {
            // Call the adapter's public methods directly
            
            switch (request.Method)
            {
                case "getState":
                    return await adapter.GetStateAsync();

                case "listActions":
                    return await adapter.ListActionsAsync();

                case "navigate":
                    if (request.Params.HasValue && request.Params.Value.TryGetProperty("path", out var pathProp))
                    {
                        var path = pathProp.GetString();
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
                                paths.Add(p.GetString());
                        }
                    }
                    return await adapter.GetMetadataAsync(paths);

                case "executeAction":
                    if (request.Params.HasValue && request.Params.Value.TryGetProperty("actionId", out var actionIdProp))
                    {
                        var actionId = actionIdProp.GetString();
                        return await adapter.ExecuteActionAsync(actionId);
                    }
                    throw new JsonRpcException(JsonRpcException.InvalidParams, "Missing actionId parameter");

                default:
                    throw new JsonRpcException(JsonRpcException.MethodNotFound, 
                        $"Method '{request.Method}' not implemented");
            }
        }
    }
}