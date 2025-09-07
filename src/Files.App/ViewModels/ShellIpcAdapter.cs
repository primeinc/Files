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
    // Adapter with strict allow list, path normalization, selection cap and structured errors.
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

        // Public methods for IpcCoordinator to call
        public async Task<object> GetStateAsync()
        {
            // Must run on UI thread to access Frame properties
            var tcs = new TaskCompletionSource<object>();
            
            await _uiQueue.EnqueueAsync(async () =>
            {
                try
                {
                    var state = new
                    {
                        currentPath = _nav.CurrentPath ?? _shell.WorkingDirectory,
                        canNavigateBack = _nav.CanGoBack,
                        canNavigateForward = _nav.CanGoForward,
                        isLoading = _shell.FilesAndFolders.Count == 0,
                        itemCount = _shell.FilesAndFolders.Count
                    };
                    tcs.SetResult(state);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
                await Task.CompletedTask;
            }).ConfigureAwait(false);
            
            return await tcs.Task.ConfigureAwait(false);
        }

        public async Task<object> ListActionsAsync()
        {
            var tcs = new TaskCompletionSource<object>();
            
            await _uiQueue.EnqueueAsync(async () =>
            {
                try
                {
                    var actions = _actions.GetAllowedActions().Select(actionId => new
                    {
                        id = actionId,
                        name = actionId,
                        description = $"Execute {actionId} action"
                    }).ToArray();
                    tcs.SetResult(new { actions });
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
                await Task.CompletedTask;
            }).ConfigureAwait(false);
            
            return await tcs.Task.ConfigureAwait(false);
        }

        public async Task<object> NavigateAsync(string path)
        {
            if (!TryNormalizePath(path, out var normalizedPath))
            {
                throw new JsonRpcException(JsonRpcException.InvalidParams, "Invalid path");
            }

            await _uiQueue.EnqueueAsync(async () =>
            {
                await NavigateToPathNormalized(normalizedPath);
            }).ConfigureAwait(false);

            return new { status = "ok" };
        }

        public async Task<object> GetMetadataAsync(List<string> paths)
        {
            // GetFileMetadata uses file system, doesn't need UI thread
            return await Task.Run(() =>
            {
                var metadata = GetFileMetadata(paths);
                return new { items = metadata };
            }).ConfigureAwait(false);
        }

        public async Task<object> ExecuteActionAsync(string actionId)
        {
            if (string.IsNullOrEmpty(actionId) || !_actions.CanExecute(actionId))
            {
                throw new JsonRpcException(JsonRpcException.InvalidParams, "Action not found or cannot execute");
            }

            await _uiQueue.EnqueueAsync(async () =>
            {
                await ExecuteActionById(actionId);
            }).ConfigureAwait(false);

            return new { status = "ok" };
        }

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
                if (p.StartsWith(Constants.PathValidationConstants.MTP_DEVICE_PREFIX) || 
                    p.StartsWith(Constants.PathValidationConstants.DEVICE_NAMESPACE_PREFIX))
                    return false;

                normalized = p;
                return true;
            }
            catch
            {
                return false;
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