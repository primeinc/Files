using Files.App.Communication;
using Files.App.Communication.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using System.Threading;
using System.IO;
using Microsoft.Extensions.Logging;
using Files.App.Data.Contracts;

namespace Files.App.ViewModels
{
    // Adapter with strict allowlist, path normalization, selection cap and structured errors.
    public sealed class ShellIpcAdapter
    {
        private readonly ShellViewModel _shell;
        private readonly IAppCommunicationService _comm;
        private readonly ActionRegistry _actions;
        private readonly RpcMethodRegistry _methodRegistry;
        private readonly UIOperationQueue _uiQueue;
        private readonly ILogger<ShellIpcAdapter> _logger;
        private readonly INavigationStateProvider _nav;

        private readonly TimeSpan _coalesceWindow = TimeSpan.FromMilliseconds(100);
        private DateTime _lastWdmNotif = DateTime.MinValue;

        public ShellIpcAdapter(
            ShellViewModel shell,
            IAppCommunicationService comm,
            ActionRegistry actions,
            RpcMethodRegistry methodRegistry,
            DispatcherQueue dispatcher,
            ILogger<ShellIpcAdapter> logger,
            INavigationStateProvider nav)
        {
            _shell = shell ?? throw new ArgumentNullException(nameof(shell));
            _comm = comm ?? throw new ArgumentNullException(nameof(comm));
            _actions = actions ?? throw new ArgumentNullException(nameof(actions));
            _methodRegistry = methodRegistry ?? throw new ArgumentNullException(nameof(methodRegistry));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _nav = nav ?? throw new ArgumentNullException(nameof(nav));
            _uiQueue = new UIOperationQueue(dispatcher ?? throw new ArgumentNullException(nameof(dispatcher)));

            _comm.OnRequestReceived += HandleRequestAsync;

            _shell.WorkingDirectoryModified += Shell_WorkingDirectoryModified;
            _nav.StateChanged += Nav_StateChanged;
        }

        private async void Nav_StateChanged(object? sender, EventArgs e)
        {
            try
            {
                var notif = new JsonRpcMessage
                {
                    Method = "navigationStateChanged",
                    Params = JsonSerializer.SerializeToElement(new { canNavigateBack = _nav.CanGoBack, canNavigateForward = _nav.CanGoForward, path = _nav.CurrentPath })
                };
                await _comm.BroadcastAsync(notif).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting navigation state change");
            }
        }

        private async void Shell_WorkingDirectoryModified(object? sender, WorkingDirectoryModifiedEventArgs e)
        {
            var now = DateTime.UtcNow;
            if (now - _lastWdmNotif < _coalesceWindow) return;
            _lastWdmNotif = now;

            try
            {
                var notif = new JsonRpcMessage
                {
                    Method = "workingDirectoryChanged",
                    Params = JsonSerializer.SerializeToElement(new { path = e.Path, name = e.Name, isLibrary = e.IsLibrary })
                };

                await _comm.BroadcastAsync(notif).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting working directory change");
            }
        }

        // This method would need to be wired to the actual selection change event in ShellViewModel
        public async void OnSelectionChanged(IEnumerable<string> selectedPaths)
        {
            try
            {
                var summary = selectedPaths?.Select(p => new {
                    path = p,
                    name = Path.GetFileName(p),
                    isDir = Directory.Exists(p)
                }) ?? Enumerable.Empty<object>();

                var list = summary.Take(IpcConfig.SelectionNotificationCap).ToArray();
                var notif = new JsonRpcMessage
                {
                    Method = "selectionChanged",
                    Params = JsonSerializer.SerializeToElement(new {
                        items = list,
                        truncated = (summary.Count() > IpcConfig.SelectionNotificationCap)
                    })
                };

                await _comm.BroadcastAsync(notif).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting selection change");
            }
        }

        private static bool TryNormalizePath(string raw, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            if (raw.IndexOf('\0') >= 0) return false;

            try
            {
                var p = Path.GetFullPath(raw);
                // Reject device paths and odd prefixes
                if (p.StartsWith(@"\\?\") || p.StartsWith(@"\\.\"))
                    return false;

                normalized = p;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task HandleRequestAsync(ClientContext client, JsonRpcMessage request)
        {
            try
            {
                // Basic validation
                if (!JsonRpcMessage.ValidJsonRpc(request) || JsonRpcMessage.IsInvalidRequest(request))
                {
                    if (!request.IsNotification)
                        await _comm.SendResponseAsync(client, JsonRpcMessage.MakeError(request.Id, -32600, "Invalid JSON-RPC")).ConfigureAwait(false);
                    return;
                }

                // Check method registry for authorization
                if (!_methodRegistry.TryGet(request.Method ?? "", out var methodDef))
                {
                    if (!request.IsNotification)
                        await _comm.SendResponseAsync(client, JsonRpcMessage.MakeError(request.Id, -32601, "Method not found")).ConfigureAwait(false);
                    return;
                }

                // Check payload size limit if defined
                if (methodDef.MaxPayloadBytes.HasValue)
                {
                    var payloadSize = System.Text.Encoding.UTF8.GetByteCount(request.ToJson());
                    if (payloadSize > methodDef.MaxPayloadBytes.Value)
                    {
                        if (!request.IsNotification)
                            await _comm.SendResponseAsync(client, JsonRpcMessage.MakeError(request.Id, -32602, "Payload too large")).ConfigureAwait(false);
                        return;
                    }
                }

                // Route to specific handlers
                switch (request.Method)
                {
                    case "getState":
                        await HandleGetState(client, request).ConfigureAwait(false);
                        break;

                    case "listActions":
                        await HandleListActions(client, request).ConfigureAwait(false);
                        break;

                    case "executeAction":
                        await HandleExecuteAction(client, request).ConfigureAwait(false);
                        break;

                    case "navigate":
                        await HandleNavigate(client, request).ConfigureAwait(false);
                        break;

                    case "getMetadata":
                        await HandleGetMetadata(client, request).ConfigureAwait(false);
                        break;

                    default:
                        if (!request.IsNotification)
                            await _comm.SendResponseAsync(client, JsonRpcMessage.MakeError(request.Id, -32601, "Method not implemented")).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling request from client {ClientId}", client.Id);
                if (!request.IsNotification)
                {
                    await _comm.SendResponseAsync(client, JsonRpcMessage.MakeError(request.Id, -32000, "Internal server error")).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleGetState(ClientContext client, JsonRpcMessage request)
        {
            try
            {
                var state = new
                {
                    currentPath = _nav.CurrentPath ?? _shell.WorkingDirectory,
                    canNavigateBack = _nav.CanGoBack,
                    canNavigateForward = _nav.CanGoForward,
                    isLoading = _shell.FilesAndFolders.Count == 0, // Simple loading check
                    itemCount = _shell.FilesAndFolders.Count
                };

                if (!request.IsNotification)
                {
                    await _comm.SendResponseAsync(client, JsonRpcMessage.MakeResult(request.Id, state));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting state");
                if (!request.IsNotification)
                {
                    await _comm.SendResponseAsync(client, JsonRpcMessage.MakeError(request.Id, -32000, "Failed to get state"));
                }
            }
        }

        private async Task HandleListActions(ClientContext client, JsonRpcMessage request)
        {
            try
            {
                var actions = _actions.GetAllowedActions().Select(actionId => new
                {
                    id = actionId,
                    name = actionId, // Could be enhanced with proper display names
                    description = $"Execute {actionId} action"
                }).ToArray();

                if (!request.IsNotification)
                {
                    await _comm.SendResponseAsync(client, JsonRpcMessage.MakeResult(request.Id, new { actions }));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing actions");
                if (!request.IsNotification)
                {
                    await _comm.SendResponseAsync(client, JsonRpcMessage.MakeError(request.Id, -32000, "Failed to list actions"));
                }
            }
        }

        private async Task HandleExecuteAction(ClientContext client, JsonRpcMessage request)
        {
            try
            {
                if (request.Params is null || !request.Params.Value.TryGetProperty("actionId", out var aidProp))
                {
                    if (!request.IsNotification)
                        await _comm.SendResponseAsync(client, JsonRpcMessage.MakeError(request.Id, -32602, "Missing actionId"));
                    return;
                }

                var actionId = aidProp.GetString();
                if (string.IsNullOrEmpty(actionId) || !_actions.CanExecute(actionId))
                {
                    if (!request.IsNotification)
                        await _comm.SendResponseAsync(client, JsonRpcMessage.MakeError(request.Id, -32601, "Action not found or cannot execute"));
                    return;
                }

                // Execute on UI thread
                await _uiQueue.EnqueueAsync(async () =>
                {
                    await ExecuteActionById(actionId);
                }).ConfigureAwait(false);

                if (!request.IsNotification)
                    await _comm.SendResponseAsync(client, JsonRpcMessage.MakeResult(request.Id, new { status = "ok" }));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing action");
                if (!request.IsNotification)
                    await _comm.SendResponseAsync(client, JsonRpcMessage.MakeError(request.Id, -32000, "Failed to execute action"));
            }
        }

        private async Task HandleNavigate(ClientContext client, JsonRpcMessage request)
        {
            try
            {
                if (request.Params is null || !request.Params.Value.TryGetProperty("path", out var pathProp))
                {
                    if (!request.IsNotification)
                        await _comm.SendResponseAsync(client, JsonRpcMessage.MakeError(request.Id, -32602, "Missing path"));
                    return;
                }

                var rawPath = pathProp.GetString();
                if (!TryNormalizePath(rawPath!, out var normalizedPath))
                {
                    if (!request.IsNotification)
                        await _comm.SendResponseAsync(client, JsonRpcMessage.MakeError(request.Id, -32602, "Invalid path"));
                    return;
                }

                await _uiQueue.EnqueueAsync(async () =>
                {
                    await NavigateToPathNormalized(normalizedPath);
                }).ConfigureAwait(false);

                if (!request.IsNotification)
                    await _comm.SendResponseAsync(client, JsonRpcMessage.MakeResult(request.Id, new { status = "ok" }));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error navigating");
                if (!request.IsNotification)
                    await _comm.SendResponseAsync(client, JsonRpcMessage.MakeError(request.Id, -32000, "Failed to navigate"));
            }
        }

        private async Task HandleGetMetadata(ClientContext client, JsonRpcMessage request)
        {
            try
            {
                if (request.Params is null || !request.Params.Value.TryGetProperty("paths", out var pathsElem) || pathsElem.ValueKind != JsonValueKind.Array)
                {
                    if (!request.IsNotification)
                        await _comm.SendResponseAsync(client, JsonRpcMessage.MakeError(request.Id, -32602, "Missing paths array"));
                    return;
                }

                var paths = new List<string>();
                foreach (var p in pathsElem.EnumerateArray())
                {
                    if (p.ValueKind == JsonValueKind.String && paths.Count < IpcConfig.GetMetadataMaxItems)
                        paths.Add(p.GetString()!);
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(client.Cancellation?.Token ?? CancellationToken.None);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(IpcConfig.GetMetadataTimeoutSec));

                var metadata = await Task.Run(() => GetFileMetadata(paths), timeoutCts.Token).ConfigureAwait(false);

                if (!request.IsNotification)
                {
                    await _comm.SendResponseAsync(client, JsonRpcMessage.MakeResult(request.Id, new { items = metadata }));
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("GetMetadata operation timed out for client {ClientId}", client.Id);
                if (!request.IsNotification)
                    await _comm.SendResponseAsync(client, JsonRpcMessage.MakeError(request.Id, -32000, "Operation timed out"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting metadata");
                if (!request.IsNotification)
                    await _comm.SendResponseAsync(client, JsonRpcMessage.MakeError(request.Id, -32000, "Failed to get metadata"));
            }
        }

        private List<ItemDto> GetFileMetadata(List<string> paths)
        {
            var results = new List<ItemDto>();
            
            foreach (var path in paths)
            {
                try
                {
                    var item = new ItemDto { Path = path, Name = Path.GetFileName(path) };
                    
                    if (File.Exists(path))
                    {
                        var fi = new FileInfo(path);
                        item.IsDirectory = false;
                        item.SizeBytes = fi.Length;
                        item.DateModified = fi.LastWriteTime.ToString("o");
                        item.DateCreated = fi.CreationTime.ToString("o");
                        item.Exists = true;
                    }
                    else if (Directory.Exists(path))
                    {
                        var di = new DirectoryInfo(path);
                        item.IsDirectory = true;
                        item.SizeBytes = 0;
                        item.DateModified = di.LastWriteTime.ToString("o");
                        item.DateCreated = di.CreationTime.ToString("o");
                        item.Exists = true;
                    }
                    else
                    {
                        item.Exists = false;
                    }
                    
                    results.Add(item);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error getting metadata for path: {Path}", path);
                    results.Add(new ItemDto 
                    { 
                        Path = path, 
                        Name = Path.GetFileName(path), 
                        Exists = false 
                    });
                }
            }
            
            return results;
        }

        private async Task ExecuteActionById(string actionId)
        {
            _logger.LogInformation("Executing action: {ActionId}", actionId);
            await Task.CompletedTask;
        }

        private async Task NavigateToPathNormalized(string path)
        {
            _logger.LogInformation("Navigating to path: {Path}", path);
            await _nav.NavigateToAsync(path);
        }
    }
}