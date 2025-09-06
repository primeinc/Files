using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Files.App.Communication
{
    public sealed class WebSocketAppCommunicationService : IAppCommunicationService, IDisposable
    {
        private readonly HttpListener _httpListener;
        private readonly RpcMethodRegistry _methodRegistry;
        private readonly ILogger<WebSocketAppCommunicationService> _logger;
        private readonly ConcurrentDictionary<Guid, ClientContext> _clients = new();
        private readonly Timer _keepaliveTimer;
        private readonly Timer _cleanupTimer;
        private readonly CancellationTokenSource _cancellation = new();
        
        private string? _currentToken;
        private int _currentEpoch;
        private bool _isStarted;

        public event Func<ClientContext, JsonRpcMessage, Task>? OnRequestReceived;

        public WebSocketAppCommunicationService(
            RpcMethodRegistry methodRegistry,
            ILogger<WebSocketAppCommunicationService> logger)
        {
            _methodRegistry = methodRegistry ?? throw new ArgumentNullException(nameof(methodRegistry));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpListener = new HttpListener();
            
            // Setup keepalive timer (every 30 seconds)
            _keepaliveTimer = new Timer(SendKeepalive, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            
            // Setup cleanup timer (every 60 seconds)
            _cleanupTimer = new Timer(CleanupInactiveClients, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }

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
                _httpListener.Prefixes.Add("http://127.0.0.1:52345/");
                _httpListener.Start();
                _isStarted = true;

                _ = Task.Run(AcceptConnectionsAsync, _cancellation.Token);
                
                _logger.LogInformation("WebSocket IPC service started on http://127.0.0.1:52345/");
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
            var buffer = new byte[4096];
            var messageBuilder = new StringBuilder();
            var totalReceived = 0;

            try
            {
                while (client.WebSocket?.State == WebSocketState.Open && !client.Cancellation!.Token.IsCancellationRequested)
                {
                    var result = await client.WebSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), 
                        client.Cancellation.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    if (result.MessageType != WebSocketMessageType.Text)
                        continue;

                    totalReceived += result.Count;
                    if (totalReceived > IpcConfig.WebSocketMaxMessageBytes)
                    {
                        _logger.LogWarning("Client {ClientId} exceeded max message size, disconnecting", client.Id);
                        break;
                    }

                    var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    messageBuilder.Append(text);

                    if (result.EndOfMessage)
                    {
                        var messageText = messageBuilder.ToString();
                        messageBuilder.Clear();
                        totalReceived = 0;

                        client.LastSeenUtc = DateTime.UtcNow;
                        await ProcessIncomingMessage(client, messageText);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException ex)
            {
                _logger.LogDebug("WebSocket error for client {ClientId}: {Error}", client.Id, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in receive loop for client {ClientId}", client.Id);
            }
        }

        private async Task ProcessIncomingMessage(ClientContext client, string messageText)
        {
            var message = JsonRpcMessage.FromJson(messageText);
            if (!JsonRpcMessage.ValidJsonRpc(message) || JsonRpcMessage.IsInvalidRequest(message!))
            {
                if (!message?.IsNotification == true)
                {
                    var errorResponse = JsonRpcMessage.MakeError(message?.Id, -32600, "Invalid Request");
                    await SendResponseAsync(client, errorResponse);
                }
                return;
            }

            // Handle handshake specially
            if (message!.Method == "handshake")
            {
                await HandleHandshake(client, message);
                return;
            }

            // Check method registry
            if (!_methodRegistry.TryGet(message.Method ?? "", out var methodDef))
            {
                if (!message.IsNotification)
                {
                    var errorResponse = JsonRpcMessage.MakeError(message.Id, -32601, "Method not found");
                    await SendResponseAsync(client, errorResponse);
                }
                return;
            }

            // Enforce authentication
            if (methodDef.RequiresAuth && !client.IsAuthenticated)
            {
                if (!message.IsNotification)
                {
                    var errorResponse = JsonRpcMessage.MakeError(message.Id, -32001, "Authentication required");
                    await SendResponseAsync(client, errorResponse);
                }
                return;
            }

            // Rate limiting
            if (!client.TryConsumeToken())
            {
                if (!message.IsNotification)
                {
                    var errorResponse = JsonRpcMessage.MakeError(message.Id, -32003, "Rate limit exceeded");
                    await SendResponseAsync(client, errorResponse);
                }
                return;
            }

            // Check if notifications are allowed for this method
            if (message.IsNotification && !methodDef.AllowNotifications)
            {
                _logger.LogWarning("Client {ClientId} sent notification for method {Method} which doesn't allow notifications", 
                    client.Id, message.Method);
                return;
            }

            // Dispatch to handlers
            OnRequestReceived?.Invoke(client, message);
        }

        private async Task HandleHandshake(ClientContext client, JsonRpcMessage message)
        {
            try
            {
                if (message.Params?.TryGetProperty("token", out var tokenProp) != true)
                {
                    var errorResponse = JsonRpcMessage.MakeError(message.Id, -32602, "Missing token parameter");
                    await SendResponseAsync(client, errorResponse);
                    return;
                }

                var providedToken = tokenProp.GetString();
                if (providedToken != _currentToken)
                {
                    var errorResponse = JsonRpcMessage.MakeError(message.Id, -32002, "Invalid token");
                    await SendResponseAsync(client, errorResponse);
                    return;
                }

                client.IsAuthenticated = true;
                client.AuthEpoch = _currentEpoch;
                
                if (message.Params?.TryGetProperty("clientInfo", out var clientInfoProp) == true)
                {
                    client.ClientInfo = clientInfoProp.GetString();
                }

                if (!message.IsNotification)
                {
                    var successResponse = JsonRpcMessage.MakeResult(message.Id, new { 
                        status = "authenticated", 
                        epoch = _currentEpoch,
                        serverInfo = "Files IPC Server"
                    });
                    await SendResponseAsync(client, successResponse);
                }

                _logger.LogInformation("Client {ClientId} authenticated successfully", client.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during handshake with client {ClientId}", client.Id);
            }
        }

        private async Task ClientSendLoopAsync(ClientContext client)
        {
            try
            {
                while (client.WebSocket?.State == WebSocketState.Open && !client.Cancellation!.Token.IsCancellationRequested)
                {
                    if (client.SendQueue.TryDequeue(out var item))
                    {
                        var bytes = Encoding.UTF8.GetBytes(item.payload);
                        await client.WebSocket.SendAsync(
                            new ArraySegment<byte>(bytes),
                            WebSocketMessageType.Text,
                            true,
                            client.Cancellation.Token);
                            
                        client.DecreaseQueuedBytes(bytes.Length);
                    }
                    else
                    {
                        await Task.Delay(10, client.Cancellation.Token);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in send loop for client {ClientId}", client.Id);
            }
        }

        public async Task SendResponseAsync(ClientContext client, JsonRpcMessage response)
        {
            if (response.IsNotification)
            {
                _logger.LogWarning("Attempted to send notification as response");
                return;
            }

            try
            {
                var json = response.ToJson();
                client.TryEnqueue(json, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending response to client {ClientId}", client.Id);
            }
        }

        public async Task BroadcastAsync(JsonRpcMessage notification)
        {
            if (!notification.IsNotification)
            {
                _logger.LogWarning("Attempted to broadcast non-notification message");
                return;
            }

            var json = notification.ToJson();
            var method = notification.Method;

            foreach (var client in _clients.Values)
            {
                if (client.IsAuthenticated && client.TryConsumeToken())
                {
                    client.TryEnqueue(json, true, method);
                }
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

            var cutoff = DateTime.UtcNow.AddMinutes(-5);
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

        public void Dispose()
        {
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
        }
    }
}