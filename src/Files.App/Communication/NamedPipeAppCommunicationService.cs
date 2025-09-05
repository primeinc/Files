using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Windows.Storage;

namespace Files.App.Communication
{
	public sealed class NamedPipeAppCommunicationService : IAppCommunicationService, IDisposable
	{
		// Fields
		private readonly RpcMethodRegistry _methodRegistry;
		private readonly ILogger<NamedPipeAppCommunicationService> _logger;
		private readonly ConcurrentDictionary<Guid, ClientContext> _clients = new();
		private readonly Timer _keepaliveTimer;
		private readonly Timer _cleanupTimer;
		private readonly CancellationTokenSource _cancellation = new();
		private string? _currentToken;
		private int _currentEpoch;
		private string? _pipeName;
		private bool _isStarted;
		private Task? _acceptTask;
		private bool _disposed;

		// Events
		public event Func<ClientContext, JsonRpcMessage, Task>? OnRequestReceived;

		// Constructor
		public NamedPipeAppCommunicationService(
			RpcMethodRegistry methodRegistry,
			ILogger<NamedPipeAppCommunicationService> logger)
		{
			_methodRegistry = methodRegistry ?? throw new ArgumentNullException(nameof(methodRegistry));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));

			// Setup keepalive timer (every 30 seconds)
			_keepaliveTimer = new Timer(SendKeepalive, null, TimeSpan.FromSeconds(30d), TimeSpan.FromSeconds(30d));

			// Setup cleanup timer (every 60 seconds)
			_cleanupTimer = new Timer(CleanupInactiveClients, null, TimeSpan.FromSeconds(60d), TimeSpan.FromSeconds(60d));
		}

		// Public methods
		public async Task StartAsync()
		{
			if (!ProtectedTokenStore.IsEnabled())
			{
				_logger.LogWarning("Remote control is not enabled, refusing to start Named Pipe service");
				return;
			}

			if (_isStarted)
				return;

			try
			{
				_currentToken = await ProtectedTokenStore.GetOrCreateTokenAsync();
				_currentEpoch = ProtectedTokenStore.GetEpoch();

				// Generate randomized pipe name per session for security
				_pipeName = $"Files_IPC_{Environment.UserName}_{Guid.NewGuid():N}";

				_isStarted = true;
				_acceptTask = Task.Run(AcceptConnectionsAsync, _cancellation.Token);

				_logger.LogInformation("Named Pipe IPC service started with pipe: {PipeName}", _pipeName);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to start Named Pipe IPC service");
				throw;
			}
		}

		public async Task StopAsync()
		{
			if (!_isStarted)
				return;

			try
			{
				_cancellation.Cancel();

				// Wait for accept task to complete
				if (_acceptTask != null)
				{
					try
					{
						await _acceptTask;
					}
					catch (OperationCanceledException)
					{
						// Expected when cancelling
					}
				}

				// Close all client connections
				foreach (var client in _clients.Values)
				{
					client.Dispose();
				}
				_clients.Clear();

				_isStarted = false;
				_logger.LogInformation("Named Pipe IPC service stopped");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error stopping Named Pipe IPC service");
			}
		}

		public async Task SendResponseAsync(ClientContext client, JsonRpcMessage response)
		{
			if (client?.TransportHandle is not NamedPipeServerStream pipe || !pipe.IsConnected)
				return;

			try
			{
				var json = response.ToJson();
				var canEnqueue = client.TryEnqueue(json, false);
				if (!canEnqueue)
				{
					_logger.LogWarning("Client {ClientId} queue full, dropping response", client.Id);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error queuing response for client {ClientId}", client.Id);
			}
		}

		public async Task BroadcastAsync(JsonRpcMessage notification)
		{
			if (!_isStarted)
				return;

			var json = notification.ToJson();
			var activeclients = _clients.Values
				.Where(c => c.TransportHandle is NamedPipeServerStream pipe && pipe.IsConnected)
				.ToList();

			foreach (var client in activeclients)
			{
				try
				{
					var canEnqueue = client.TryEnqueue(json, true, notification.Method);
					if (!canEnqueue)
					{
						_logger.LogDebug("Client {ClientId} queue full, dropping notification {Method}", client.Id, notification.Method);
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Error queuing notification for client {ClientId}", client.Id);
				}
			}
		}

		// Private methods
		private async Task AcceptConnectionsAsync()
		{
			while (!_cancellation.Token.IsCancellationRequested)
			{
				try
				{
					var pipe = CreateSecurePipeServer();
					await pipe.WaitForConnectionAsync(_cancellation.Token);

					var client = new ClientContext
					{
						TransportHandle = pipe,
						Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token)
					};

					_clients[client.Id] = client;
					_logger.LogDebug("Named Pipe client {ClientId} connected", client.Id);

					// Start client handlers
					_ = Task.Run(() => ClientSendLoopAsync(client), client.Cancellation.Token);
					_ = Task.Run(() => ClientReceiveLoopAsync(client), client.Cancellation.Token);
				}
				catch (OperationCanceledException) when (_cancellation.Token.IsCancellationRequested)
				{
					break;
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Error accepting Named Pipe connection");
				}
			}
		}

		private NamedPipeServerStream CreateSecurePipeServer()
		{
			var currentUser = WindowsIdentity.GetCurrent();
			var pipeSecurity = new PipeSecurity();

			// Allow full control to current user only
			pipeSecurity.AddAccessRule(new PipeAccessRule(
				currentUser.User!,
				PipeAccessRights.FullControl,
				AccessControlType.Allow));

			// Deny access to everyone else
			pipeSecurity.AddAccessRule(new PipeAccessRule(
				new SecurityIdentifier(WellKnownSidType.WorldSid, null),
				PipeAccessRights.FullControl,
				AccessControlType.Deny));

			return NamedPipeServerStreamAcl.Create(
				_pipeName!,
				PipeDirection.InOut,
				NamedPipeServerStream.MaxAllowedServerInstances,
				PipeTransmissionMode.Byte,
				PipeOptions.Asynchronous | PipeOptions.WriteThrough,
				(int)IpcConfig.NamedPipeMaxMessageBytes,
				(int)IpcConfig.NamedPipeMaxMessageBytes,
				pipeSecurity);
		}

		private async Task ClientReceiveLoopAsync(ClientContext client)
		{
			var pipe = (NamedPipeServerStream)client.TransportHandle!;

			while (pipe.IsConnected && !client.Cancellation!.Token.IsCancellationRequested)
			{
				try
				{
					// Read length prefix (4 bytes)
					var lengthBuffer = new byte[4];
					var bytesRead = 0;
					while (bytesRead < 4)
					{
						var read = await pipe.ReadAsync(
							lengthBuffer.AsMemory(bytesRead, 4 - bytesRead),
							client.Cancellation.Token);
						if (read == 0)
							return; // Pipe closed

						bytesRead += read;
					}

					var messageLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
					if (messageLength <= 0 || messageLength > IpcConfig.NamedPipeMaxMessageBytes)
					{
						_logger.LogWarning("Invalid message length {Length} from client {ClientId}", messageLength, client.Id);
						return;
					}

					// Read message body
					var messageBuffer = new byte[messageLength];
					bytesRead = 0;
					while (bytesRead < messageLength)
					{
						var read = await pipe.ReadAsync(
							messageBuffer.AsMemory(bytesRead, messageLength - bytesRead),
							client.Cancellation.Token);
						if (read == 0)
							return; // Pipe closed

						bytesRead += read;
					}

					var messageText = Encoding.UTF8.GetString(messageBuffer);
					client.LastSeenUtc = DateTime.UtcNow;

					await ProcessIncomingMessageAsync(client, messageText);
				}
				catch (OperationCanceledException) when (client.Cancellation.Token.IsCancellationRequested)
				{
					break;
				}
				catch (IOException ex) when (ex.Message.Contains("pipe"))
				{
					break; // Pipe disconnected
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Error in client receive loop for {ClientId}", client.Id);
					break;
				}
			}

			// Cleanup client
			_clients.TryRemove(client.Id, out _);
			client.Dispose();
			_logger.LogDebug("Named Pipe client {ClientId} disconnected", client.Id);
		}

		private async Task ClientSendLoopAsync(ClientContext client)
		{
			var pipe = (NamedPipeServerStream)client.TransportHandle!;

			while (pipe.IsConnected && !client.Cancellation!.Token.IsCancellationRequested)
			{
				try
				{
					if (client.SendQueue.TryDequeue(out var item))
					{
						var messageBytes = Encoding.UTF8.GetBytes(item.payload);
						var lengthBytes = new byte[4];
						BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, messageBytes.Length);

						// Write length prefix first
						await pipe.WriteAsync(lengthBytes, client.Cancellation.Token);

						// Write message body
						await pipe.WriteAsync(messageBytes, client.Cancellation.Token);
						await pipe.FlushAsync(client.Cancellation.Token);

						client.DecreaseQueuedBytes(messageBytes.Length);
					}
					else
					{
						// No messages to send, wait a bit
						await Task.Delay(10, client.Cancellation.Token);
					}
				}
				catch (OperationCanceledException) when (client.Cancellation.Token.IsCancellationRequested)
				{
					break;
				}
				catch (IOException ex) when (ex.Message.Contains("pipe"))
				{
					break; // Pipe disconnected
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Error in client send loop for {ClientId}", client.Id);
					break;
				}
			}
		}

		private async Task ProcessIncomingMessageAsync(ClientContext client, string messageText)
		{
			try
			{
				// Rate limiting check
				if (!client.TryConsumeToken())
				{
					var error = JsonRpcMessage.MakeError(null, -32003, "Rate limit exceeded");
					await SendResponseAsync(client, error);
					return;
				}

				var message = JsonRpcMessage.FromJson(messageText);
				if (!JsonRpcMessage.ValidJsonRpc(message) || JsonRpcMessage.IsInvalidRequest(message))
				{
					var error = JsonRpcMessage.MakeError(message?.Id, -32600, "Invalid Request");
					await SendResponseAsync(client, error);
					return;
				}

				// Check method registry
				if (!string.IsNullOrEmpty(message.Method) && _methodRegistry.TryGet(message.Method, out var methodDef))
				{
					// Auth check
					if (methodDef.RequiresAuth && !client.IsAuthenticated)
					{
						var error = JsonRpcMessage.MakeError(message.Id, -32001, "Authentication required");
						await SendResponseAsync(client, error);
						return;
					}

					// Additional auth policy check
					if (methodDef.AuthorizationPolicy != null && !methodDef.AuthorizationPolicy(client, message))
					{
						var error = JsonRpcMessage.MakeError(message.Id, -32002, "Authorization failed");
						await SendResponseAsync(client, error);
						return;
					}
				}

				// Handle token validation for handshake
				if (message.Method == "handshake")
				{
					await HandleHandshakeAsync(client, message);
					return;
				}

				// Delegate to handler
				if (OnRequestReceived != null)
				{
					await OnRequestReceived(client, message);
				}
			}
			catch (JsonException)
			{
				var error = JsonRpcMessage.MakeError(null, -32700, "Parse error");
				await SendResponseAsync(client, error);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error processing message from client {ClientId}", client.Id);
				var error = JsonRpcMessage.MakeError(null, -32603, "Internal error");
				await SendResponseAsync(client, error);
			}
		}

		private async Task HandleHandshakeAsync(ClientContext client, JsonRpcMessage request)
		{
			try
			{
				if (request.Params?.TryGetProperty("token", out var tokenElement) == true)
				{
					var providedToken = tokenElement.GetString();
					if (string.Equals(providedToken, _currentToken, StringComparison.Ordinal))
					{
						client.IsAuthenticated = true;
						client.AuthEpoch = _currentEpoch;

						var result = JsonRpcMessage.MakeResult(request.Id, new
						{
							authenticated = true,
							epoch = _currentEpoch,
							serverVersion = "1.0"
						});

						await SendResponseAsync(client, result);
						_logger.LogInformation("Client {ClientId} authenticated successfully", client.Id);
					}
					else
					{
						var error = JsonRpcMessage.MakeError(request.Id, -32002, "Invalid token");
						await SendResponseAsync(client, error);
					}
				}
				else
				{
					var error = JsonRpcMessage.MakeError(request.Id, -32602, "Invalid params - token required");
					await SendResponseAsync(client, error);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error handling handshake for client {ClientId}", client.Id);
				var error = JsonRpcMessage.MakeError(request.Id, -32603, "Internal error");
				await SendResponseAsync(client, error);
			}
		}

		private void SendKeepalive(object? state)
		{
			if (!_isStarted || _cancellation.Token.IsCancellationRequested)
				return;

			var pingNotification = new JsonRpcMessage
			{
				Method = "ping",
				Params = JsonSerializer.SerializeToElement(new { timestamp = DateTime.UtcNow })
			};

			_ = Task.Run(async () =>
			{
				try
				{
					await BroadcastAsync(pingNotification);
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Error sending keepalive ping");
				}
			});
		}

		private void CleanupInactiveClients(object? state)
		{
			if (!_isStarted || _cancellation.Token.IsCancellationRequested)
				return;

			var cutoff = DateTime.UtcNow.AddMinutes(-5d);
			var toRemove = new List<ClientContext>();

			foreach (var client in _clients.Values)
			{
				var pipe = client.TransportHandle as NamedPipeServerStream;
				if (client.LastSeenUtc < cutoff || pipe?.IsConnected != true)
				{
					toRemove.Add(client);
				}
			}

			foreach (var client in toRemove)
			{
				_clients.TryRemove(client.Id, out _);
				client.Dispose();
				_logger.LogDebug("Cleaned up inactive client {ClientId}", client.Id);
			}
		}

		// Dispose
		public void Dispose()
		{
			if (_disposed)
				return;

			_cancellation.Cancel();
			_keepaliveTimer?.Dispose();
			_cleanupTimer?.Dispose();
			_cancellation.Dispose();

			foreach (var client in _clients.Values)
			{
				client.Dispose();
			}

			_disposed = true;
		}
	}
}