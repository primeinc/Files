using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Files.App.Communication
{
	public sealed class WebSocketAppCommunicationService : IAppCommunicationService, IDisposable
	{
		// readonly fields
		private readonly HttpListener _httpListener;
		private readonly RpcMethodRegistry _methodRegistry;
		private readonly ILogger<WebSocketAppCommunicationService> _logger;
		private readonly ConcurrentDictionary<Guid, ClientContext> _clients = new();
		private readonly Timer _keepaliveTimer;
		private readonly Timer _cleanupTimer;
		private readonly CancellationTokenSource _cancellation = new();

		// Fields
		private string? _currentToken;
		private int _currentEpoch;
		private bool _isStarted;

		// _disposed field
		private bool _disposed;

		// Events
		public event Func<ClientContext, JsonRpcMessage, Task>? OnRequestReceived;

		// Constructor
		public WebSocketAppCommunicationService(
			RpcMethodRegistry methodRegistry,
			ILogger<WebSocketAppCommunicationService> logger)
		{
			_methodRegistry = methodRegistry ?? throw new ArgumentNullException(nameof(methodRegistry));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_httpListener = new HttpListener();

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
				_logger.LogWarning("Remote control is not enabled, refusing to start WebSocket service");
				return;
			}

			if (_isStarted)
				return;

			try
			{
				_currentToken = await ProtectedTokenStore.GetOrCreateTokenAsync();
				_currentEpoch = ProtectedTokenStore.GetEpoch();

				_httpListener.Prefixes.Clear();
				_httpListener.Prefixes.Add($"http://127.0.0.1:{IpcConfig.WebSocketPort}/");
				_httpListener.Start();
				_isStarted = true;

				_ = Task.Run(AcceptConnectionsAsync, _cancellation.Token);

				_logger.LogInformation("WebSocket IPC service started on http://127.0.0.1:{Port}/", IpcConfig.WebSocketPort);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to start WebSocket IPC service");
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
				_httpListener.Stop();

				// Close all client connections
				foreach (var client in _clients.Values)
				{
					client.Dispose();
				}
				_clients.Clear();

				_isStarted = false;
				_logger.LogInformation("WebSocket IPC service stopped");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error stopping WebSocket IPC service");
			}
		}

		public async Task SendResponseAsync(ClientContext client, JsonRpcMessage response)
		{
			if (client?.WebSocket?.State != WebSocketState.Open)
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
			var activeclients = _clients.Values.Where(c => c.WebSocket?.State == WebSocketState.Open).ToList();

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
					var context = await _httpListener.GetContextAsync();
					if (context.Request.IsWebSocketRequest)
					{
						_ = Task.Run(() => HandleWebSocketConnection(context), _cancellation.Token);
					}
					else
					{
						context.Response.StatusCode = 400;
						context.Response.Close();
					}
				}
				catch (HttpListenerException) when (_cancellation.Token.IsCancellationRequested)
				{
					break;
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Error accepting WebSocket connection");
				}
			}
		}

		private async Task HandleWebSocketConnection(HttpListenerContext httpContext)
		{
			WebSocketContext? webSocketContext = null;
			ClientContext? client = null;

			try
			{
				webSocketContext = await httpContext.AcceptWebSocketAsync(null);
				var webSocket = webSocketContext.WebSocket;

				client = new ClientContext
				{
					WebSocket = webSocket,
					Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token)
				};

				_clients[client.Id] = client;
				_logger.LogDebug("WebSocket client {ClientId} connected", client.Id);

				// Start send loop
				_ = Task.Run(() => ClientSendLoopAsync(client), client.Cancellation.Token);

				// Handle receive loop
				await ClientReceiveLoopAsync(client);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in WebSocket connection handler");
			}
			finally
			{
				if (client != null)
				{
					_clients.TryRemove(client.Id, out _);
					client.Dispose();
					_logger.LogDebug("WebSocket client {ClientId} disconnected", client.Id);
				}
			}
		}

		private async Task ClientReceiveLoopAsync(ClientContext client)
		{
			var buffer = ArrayPool<byte>.Shared.Rent((int)IpcConfig.WebSocketMaxMessageBytes);
			try
			{
				var webSocket = client.WebSocket!;

				while (webSocket.State == WebSocketState.Open && !client.Cancellation!.Token.IsCancellationRequested)
				{
					try
					{
						var messageBuilder = new StringBuilder();
						WebSocketReceiveResult result;

						do
						{
							result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), client.Cancellation.Token);

							if (result.MessageType == WebSocketMessageType.Text)
							{
								var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
								messageBuilder.Append(text);
							}
							else if (result.MessageType == WebSocketMessageType.Close)
								return;

						} while (!result.EndOfMessage);

						var messageText = messageBuilder.ToString();
						if (string.IsNullOrEmpty(messageText))
							continue;

						client.LastSeenUtc = DateTime.UtcNow;
						await ProcessIncomingMessageAsync(client, messageText);
					}
					catch (OperationCanceledException) when (client.Cancellation.Token.IsCancellationRequested)
					{
						break;
					}
					catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
					{
						break;
					}
					catch (Exception ex)
					{
						_logger.LogError(ex, "Error in client receive loop for {ClientId}", client.Id);
						break;
					}
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}
		}

		private async Task ClientSendLoopAsync(ClientContext client)
		{
			var webSocket = client.WebSocket!;

			while (webSocket.State == WebSocketState.Open && !client.Cancellation!.Token.IsCancellationRequested)
			{
				try
				{
					if (client.SendQueue.TryDequeue(out var item))
					{
						var bytes = Encoding.UTF8.GetBytes(item.payload);
						await webSocket.SendAsync(
							new ArraySegment<byte>(bytes),
							WebSocketMessageType.Text,
							true,
							client.Cancellation.Token);

						client.DecreaseQueuedBytes(bytes.Length);
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
				catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
				{
					break;
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
				if (client.LastSeenUtc < cutoff || client.WebSocket?.State != WebSocketState.Open)
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
			_httpListener?.Stop();
			_httpListener?.Close();
			_cancellation.Dispose();

			foreach (var client in _clients.Values)
			{
				client.Dispose();
			}

			_disposed = true;
		}
	}
}