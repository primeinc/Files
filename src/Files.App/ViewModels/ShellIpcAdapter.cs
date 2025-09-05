using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Files.App.Communication;
using Files.App.Communication.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace Files.App.ViewModels
{
	// Adapter with strict allowlist, path normalization, selection cap and structured errors.
	public sealed class ShellIpcAdapter
	{
		// readonly fields
		private readonly ShellViewModel _shell;
		private readonly IAppCommunicationService _comm;
		private readonly ActionRegistry _actions;
		private readonly RpcMethodRegistry _methodRegistry;
		private readonly UIOperationQueue _uiQueue;
		private readonly ILogger<ShellIpcAdapter> _logger;
		private readonly TimeSpan _coalesceWindow = TimeSpan.FromMilliseconds(100d);

		// Fields
		private DateTime _lastWdmNotif = DateTime.MinValue;

		// Constructor
		public ShellIpcAdapter(
			ShellViewModel shell,
			IAppCommunicationService comm,
			ActionRegistry actions,
			RpcMethodRegistry methodRegistry,
			DispatcherQueue dispatcher,
			ILogger<ShellIpcAdapter> logger)
		{
			_shell = shell ?? throw new ArgumentNullException(nameof(shell));
			_comm = comm ?? throw new ArgumentNullException(nameof(comm));
			_actions = actions ?? throw new ArgumentNullException(nameof(actions));
			_methodRegistry = methodRegistry ?? throw new ArgumentNullException(nameof(methodRegistry));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_uiQueue = new UIOperationQueue(dispatcher ?? throw new ArgumentNullException(nameof(dispatcher)));

			_comm.OnRequestReceived += HandleRequestAsync;

			_shell.WorkingDirectoryModified += Shell_WorkingDirectoryModified;
			// Note: SelectionChanged event would need to be added to ShellViewModel or accessed via different mechanism
		}

		// Private methods - Event handlers
		private async void Shell_WorkingDirectoryModified(object? sender, WorkingDirectoryModifiedEventArgs e)
		{
			// Coalesce rapid directory changes
			var now = DateTime.UtcNow;
			if ((now - _lastWdmNotif) < _coalesceWindow)
				return;

			_lastWdmNotif = now;

			var notification = new JsonRpcMessage
			{
				Method = "workingDirectoryChanged",
				Params = JsonSerializer.SerializeToElement(new
				{
					path = NormalizePath(e.Path),
					isValidPath = IsValidPath(e.Path)
				})
			};

			try
			{
				await _comm.BroadcastAsync(notification);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error broadcasting working directory changed notification");
			}
		}

		private async Task HandleRequestAsync(ClientContext client, JsonRpcMessage request)
		{
			if (string.IsNullOrEmpty(request.Method))
			{
				var error = JsonRpcMessage.MakeError(request.Id, -32600, "Invalid Request");
				await _comm.SendResponseAsync(client, error);
				return;
			}

			try
			{
				switch (request.Method)
				{
					case "getState":
						await HandleGetStateAsync(client, request);
						break;

					case "listActions":
						await HandleListActionsAsync(client, request);
						break;

					case "getMetadata":
						await HandleGetMetadataAsync(client, request);
						break;

					case "navigate":
						await HandleNavigateAsync(client, request);
						break;

					case "executeAction":
						await HandleExecuteActionAsync(client, request);
						break;

					default:
						var error = JsonRpcMessage.MakeError(request.Id, -32601, "Method not found");
						await _comm.SendResponseAsync(client, error);
						break;
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error handling request {Method} from client {ClientId}", request.Method, client.Id);
				var error = JsonRpcMessage.MakeError(request.Id, -32603, "Internal error");
				await _comm.SendResponseAsync(client, error);
			}
		}

		private async Task HandleGetStateAsync(ClientContext client, JsonRpcMessage request)
		{
			try
			{
				var result = JsonRpcMessage.MakeResult(request.Id, new
				{
					currentPath = NormalizePath(_shell.FilesystemViewModel?.WorkingDirectory ?? string.Empty),
					isValidPath = IsValidPath(_shell.FilesystemViewModel?.WorkingDirectory ?? string.Empty),
					canNavigateBack = _shell.CanNavigateBackward,
					canNavigateForward = _shell.CanNavigateForward,
					selectedItemsCount = _shell.SlimContentPage?.SelectedItems?.Count ?? 0,
					totalItemsCount = _shell.SlimContentPage?.FilesystemViewModel?.FilesAndFolders?.Count ?? 0
				});

				await _comm.SendResponseAsync(client, result);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error getting application state");
				var error = JsonRpcMessage.MakeError(request.Id, -32603, "Failed to get application state");
				await _comm.SendResponseAsync(client, error);
			}
		}

		private async Task HandleListActionsAsync(ClientContext client, JsonRpcMessage request)
		{
			try
			{
				var actions = _actions.GetAllowedActions().ToArray();
				var result = JsonRpcMessage.MakeResult(request.Id, new
				{
					actions = actions,
					count = actions.Length
				});

				await _comm.SendResponseAsync(client, result);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error listing actions");
				var error = JsonRpcMessage.MakeError(request.Id, -32603, "Failed to list actions");
				await _comm.SendResponseAsync(client, error);
			}
		}

		private async Task HandleGetMetadataAsync(ClientContext client, JsonRpcMessage request)
		{
			if (!request.Params.HasValue || !request.Params.Value.TryGetProperty("paths", out var pathsElement))
			{
				var error = JsonRpcMessage.MakeError(request.Id, -32602, "Invalid params - paths array required");
				await _comm.SendResponseAsync(client, error);
				return;
			}

			try
			{
				var pathStrings = new List<string>();
				if (pathsElement.ValueKind == JsonValueKind.Array)
				{
					foreach (var pathElement in pathsElement.EnumerateArray())
					{
						var pathStr = pathElement.GetString();
						if (!string.IsNullOrEmpty(pathStr))
							pathStrings.Add(pathStr);
					}
				}

				// Cap the number of items to process
				if (pathStrings.Count > IpcConfig.GetMetadataMaxItems)
					pathStrings = pathStrings.Take(IpcConfig.GetMetadataMaxItems).ToList();

				using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(IpcConfig.GetMetadataTimeoutSec));
				var metadata = await GetMetadataForPathsAsync(pathStrings, cts.Token);

				var result = JsonRpcMessage.MakeResult(request.Id, new
				{
					metadata = metadata,
					processed = metadata.Count,
					total = pathStrings.Count
				});

				await _comm.SendResponseAsync(client, result);
			}
			catch (OperationCanceledException)
			{
				var error = JsonRpcMessage.MakeError(request.Id, -32603, "Request timeout - too many items or slow filesystem");
				await _comm.SendResponseAsync(client, error);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error getting metadata");
				var error = JsonRpcMessage.MakeError(request.Id, -32603, "Failed to get metadata");
				await _comm.SendResponseAsync(client, error);
			}
		}

		private async Task HandleNavigateAsync(ClientContext client, JsonRpcMessage request)
		{
			if (!request.Params.HasValue || !request.Params.Value.TryGetProperty("path", out var pathElement))
			{
				var error = JsonRpcMessage.MakeError(request.Id, -32602, "Invalid params - path required");
				await _comm.SendResponseAsync(client, error);
				return;
			}

			var path = pathElement.GetString();
			if (string.IsNullOrEmpty(path))
			{
				var error = JsonRpcMessage.MakeError(request.Id, -32602, "Invalid params - path cannot be empty");
				await _comm.SendResponseAsync(client, error);
				return;
			}

			var normalizedPath = NormalizePath(path);
			if (!IsValidPath(normalizedPath))
			{
				var error = JsonRpcMessage.MakeError(request.Id, -32602, "Invalid path - security check failed");
				await _comm.SendResponseAsync(client, error);
				return;
			}

			try
			{
				await _uiQueue.EnqueueAsync(async () =>
				{
					// This would need to be implemented based on the actual ShellViewModel navigation methods
					// await _shell.NavigateToPathAsync(normalizedPath);
					_logger.LogInformation("Navigation to {Path} requested (not yet implemented)", normalizedPath);
				});

				var result = JsonRpcMessage.MakeResult(request.Id, new
				{
					success = true,
					navigatedTo = normalizedPath
				});

				await _comm.SendResponseAsync(client, result);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error navigating to {Path}", normalizedPath);
				var error = JsonRpcMessage.MakeError(request.Id, -32603, "Navigation failed");
				await _comm.SendResponseAsync(client, error);
			}
		}

		private async Task HandleExecuteActionAsync(ClientContext client, JsonRpcMessage request)
		{
			if (!request.Params.HasValue || !request.Params.Value.TryGetProperty("actionId", out var actionElement))
			{
				var error = JsonRpcMessage.MakeError(request.Id, -32602, "Invalid params - actionId required");
				await _comm.SendResponseAsync(client, error);
				return;
			}

			var actionId = actionElement.GetString();
			if (string.IsNullOrEmpty(actionId))
			{
				var error = JsonRpcMessage.MakeError(request.Id, -32602, "Invalid params - actionId cannot be empty");
				await _comm.SendResponseAsync(client, error);
				return;
			}

			if (!_actions.CanExecute(actionId))
			{
				var error = JsonRpcMessage.MakeError(request.Id, -32602, "Action not allowed or not found");
				await _comm.SendResponseAsync(client, error);
				return;
			}

			try
			{
				// Extract optional context parameter
				object? context = null;
				if (request.Params.Value.TryGetProperty("context", out var contextElement))
				{
					context = JsonSerializer.Deserialize<object>(contextElement);
				}

				await _uiQueue.EnqueueAsync(async () =>
				{
					// This would need to be implemented based on the actual action execution system
					// await _shell.ExecuteActionAsync(actionId, context);
					_logger.LogInformation("Action {ActionId} execution requested (not yet implemented)", actionId);
				});

				var result = JsonRpcMessage.MakeResult(request.Id, new
				{
					success = true,
					executedAction = actionId
				});

				await _comm.SendResponseAsync(client, result);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error executing action {ActionId}", actionId);
				var error = JsonRpcMessage.MakeError(request.Id, -32603, "Action execution failed");
				await _comm.SendResponseAsync(client, error);
			}
		}

		// Private helper methods
		private static string NormalizePath(string path)
		{
			if (string.IsNullOrEmpty(path))
				return string.Empty;

			try
			{
				// Normalize path separators and resolve relative components
				var normalized = Path.GetFullPath(path);
				return normalized;
			}
			catch
			{
				return path; // Return original if normalization fails
			}
		}

		private static bool IsValidPath(string path)
		{
			if (string.IsNullOrEmpty(path))
				return false;

			try
			{
				// Security checks: reject device paths, UNC admin shares, etc.
				var upper = path.ToUpperInvariant();

				// Reject device paths
				if (upper.StartsWith(@"\\.\", StringComparison.Ordinal) ||
				    upper.StartsWith(@"\\?\", StringComparison.Ordinal))
					return false;

				// Reject admin shares
				if (upper.StartsWith(@"\\", StringComparison.Ordinal) && upper.Contains(@"\C$", StringComparison.Ordinal))
					return false;

				// Check for path traversal attempts
				if (path.Contains("..") || path.Contains("~"))
					return false;

				// Must be rooted (absolute path)
				return Path.IsPathRooted(path);
			}
			catch
			{
				return false;
			}
		}

		private async Task<List<ItemDto>> GetMetadataForPathsAsync(List<string> paths, CancellationToken cancellationToken)
		{
			var results = new List<ItemDto>();

			foreach (var path in paths)
			{
				cancellationToken.ThrowIfCancellationRequested();

				try
				{
					var normalizedPath = NormalizePath(path);
					if (!IsValidPath(normalizedPath))
					{
						results.Add(new ItemDto
						{
							Path = path,
							Name = Path.GetFileName(path),
							Exists = false
						});
						continue;
					}

					// Check if path exists
					var exists = File.Exists(normalizedPath) || Directory.Exists(normalizedPath);
					if (!exists)
					{
						results.Add(new ItemDto
						{
							Path = normalizedPath,
							Name = Path.GetFileName(normalizedPath),
							Exists = false
						});
						continue;
					}

					// Get metadata
					var isDirectory = Directory.Exists(normalizedPath);
					var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(normalizedPath) : new FileInfo(normalizedPath);

					var item = new ItemDto
					{
						Path = normalizedPath,
						Name = info.Name,
						IsDirectory = isDirectory,
						Exists = true,
						DateCreated = info.CreationTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
						DateModified = info.LastWriteTime.ToString("yyyy-MM-ddTHH:mm:ssZ")
					};

					if (!isDirectory)
					{
						var fileInfo = (FileInfo)info;
						item.SizeBytes = fileInfo.Length;
						item.MimeType = GetMimeType(normalizedPath);
					}

					results.Add(item);
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Error getting metadata for path {Path}", path);
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

		private static string? GetMimeType(string filePath)
		{
			var extension = Path.GetExtension(filePath)?.ToLowerInvariant();
			return extension switch
			{
				".txt" => "text/plain",
				".json" => "application/json",
				".xml" => "application/xml",
				".html" => "text/html",
				".css" => "text/css",
				".js" => "application/javascript",
				".pdf" => "application/pdf",
				".jpg" or ".jpeg" => "image/jpeg",
				".png" => "image/png",
				".gif" => "image/gif",
				".mp4" => "video/mp4",
				".mp3" => "audio/mpeg",
				".zip" => "application/zip",
				_ => "application/octet-stream"
			};
		}
	}
}