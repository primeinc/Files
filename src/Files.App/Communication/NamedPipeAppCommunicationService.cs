using Microsoft.Extensions.Logging;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Files.App.Communication
{
    public sealed class NamedPipeAppCommunicationService : IAppCommunicationService, IDisposable
    {
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

        public event Func<ClientContext, JsonRpcMessage, Task>? OnRequestReceived;

        public NamedPipeAppCommunicationService(
            RpcMethodRegistry methodRegistry,
            ILogger<NamedPipeAppCommunicationService> logger)
        {
            _methodRegistry = methodRegistry ?? throw new ArgumentNullException(nameof(methodRegistry));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            
            // Setup keepalive timer (every 30 seconds)
            _keepaliveTimer = new Timer(SendKeepalive, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            
            // Setup cleanup timer (every 60 seconds)
            _cleanupTimer = new Timer(CleanupInactiveClients, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }

        public async Task StartAsync()
        {
            if (!ProtectedTokenStore.IsEnabled())
            {
                _logger.LogWarning("Remote control is not enabled, refusing to start named pipe service");
                return;
            }

            if (_isStarted)
                return;

            try
            {
                _currentToken = await ProtectedTokenStore.GetOrCreateTokenAsync();
                _currentEpoch = ProtectedTokenStore.GetEpoch();
                
                // Generate or retrieve pipe name suffix
                _pipeName = await GetOrCreatePipeNameAsync();

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
                
                if (_acceptTask != null)
                    await _acceptTask;
                
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

        private async Task<string> GetOrCreatePipeNameAsync()
        {
            var settings = ApplicationData.Current.LocalSettings;
            const string key = "Files_RemoteControl_PipeSuffix";
            
            if (settings.Values.TryGetValue(key, out var existing) && existing is string suffix && !string.IsNullOrEmpty(suffix))
            {
                var username = Environment.UserName;
                return $"FilesAppPipe_{username}_{suffix}";
            }
            
            var newSuffix = Guid.NewGuid().ToString("N")[..8];
            settings.Values[key] = newSuffix;
            var username2 = Environment.UserName;
            return $"FilesAppPipe_{username2}_{newSuffix}";
        }

        private PipeSecurity CreatePipeSecurity()
        {
            var pipeSecurity = new PipeSecurity();
            var currentUser = WindowsIdentity.GetCurrent();
            
            // Allow full control to current user
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                currentUser.User!,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            
            // Deny access to everyone else
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Deny));
                
            return pipeSecurity;
        }

        private async Task AcceptConnectionsAsync()
        {
            while (!_cancellation.Token.IsCancellationRequested)
            {
                try
                {
                    var pipeSecurity = CreatePipeSecurity();
                    var server = NamedPipeServerStreamAcl.Create(
                        _pipeName!,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        4096, 4096,
                        pipeSecurity);

                    _logger.LogDebug("Waiting for named pipe connection...");
                    await server.WaitForConnectionAsync(_cancellation.Token);
                    
                    _ = Task.Run(() => HandlePipeConnection(server), _cancellation.Token);
                }
                catch (OperationCanceledException) when (_cancellation.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error accepting named pipe connection");
                    await Task.Delay(1000, _cancellation.Token);
                }
            }
        }

        private async Task HandlePipeConnection(NamedPipeServerStream pipeServer)
        {
            ClientContext? client = null;

            try
            {
                client = new ClientContext
                {
                    TransportHandle = pipeServer,
                    Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token)
                };

                _clients[client.Id] = client;
                _logger.LogDebug("Named pipe client {ClientId} connected", client.Id);

                // Start send loop
                _ = Task.Run(() => ClientSendLoopAsync(client, pipeServer), client.Cancellation.Token);

                // Handle receive loop
                await ClientReceiveLoopAsync(client, pipeServer);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in named pipe connection handler");
            }
            finally
            {
                if (client != null)
                {
                    _clients.TryRemove(client.Id, out _);
                    client.Dispose();
                    _logger.LogDebug("Named pipe client {ClientId} disconnected", client.Id);
                }
                
                try { pipeServer.Dispose(); } catch { }
            }
        }

        private async Task ClientReceiveLoopAsync(ClientContext client, NamedPipeServerStream pipe)
        {
            var lengthBuffer = new byte[4];
            
            try
            {
                while (pipe.IsConnected && !client.Cancellation!.Token.IsCancellationRequested)
                {
                    // Read length prefix (4 bytes, little-endian)
                    int bytesRead = 0;
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
                    
                    // Validate message length
                    if (messageLength <= 0 || messageLength > IpcConfig.NamedPipeMaxMessageBytes)
                    {
                        _logger.LogWarning("Client {ClientId} sent invalid message length: {Length}", client.Id, messageLength);
                        break;
                    }

                    // Read message payload
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
                    await ProcessIncomingMessage(client, messageText);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException ex) when (ex.Message.Contains("pipe"))
            {
                _logger.LogDebug("Named pipe error for client {ClientId}: {Error}", client.Id, ex.Message);
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
                        serverInfo = "Files Named Pipe IPC Server"
                    });
                    await SendResponseAsync(client, successResponse);
                }

                _logger.LogInformation("Named pipe client {ClientId} authenticated successfully", client.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during handshake with named pipe client {ClientId}", client.Id);
            }
        }

        private async Task ClientSendLoopAsync(ClientContext client, NamedPipeServerStream pipe)
        {
            try
            {
                while (pipe.IsConnected && !client.Cancellation!.Token.IsCancellationRequested)
                {
                    if (client.SendQueue.TryDequeue(out var item))
                    {
                        var messageBytes = Encoding.UTF8.GetBytes(item.payload);
                        var lengthBytes = new byte[4];
                        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, messageBytes.Length);
                        
                        // Write length prefix
                        await pipe.WriteAsync(lengthBytes, client.Cancellation.Token);
                        
                        // Write message payload
                        await pipe.WriteAsync(messageBytes, client.Cancellation.Token);
                        await pipe.FlushAsync(client.Cancellation.Token);
                        
                        client.DecreaseQueuedBytes(messageBytes.Length);
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
                _logger.LogError(ex, "Error in send loop for named pipe client {ClientId}", client.Id);
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
                _logger.LogError(ex, "Error sending response to named pipe client {ClientId}", client.Id);
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
                _logger.LogDebug("Cleaned up inactive named pipe client {ClientId}", client.Id);
            }
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _keepaliveTimer?.Dispose();
            _cleanupTimer?.Dispose();
            _cancellation.Dispose();

            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }
        }
    }
}