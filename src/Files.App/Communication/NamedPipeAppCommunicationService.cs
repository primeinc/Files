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
        private bool _tokenUsed;

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
                // fresh ephemeral token for rendezvous
                _currentToken = EphemeralTokenHelper.GenerateToken();
                _currentEpoch = ProtectedTokenStore.GetEpoch();
                _tokenUsed = false;
                
                // Generate pipe name (always new GUID for rendezvous)
                _pipeName = $"FilesAppPipe_{Environment.UserName}_{Guid.NewGuid():N}";

                _isStarted = true;
                _acceptTask = Task.Run(AcceptConnectionsAsync, _cancellation.Token);
                
                _logger.LogInformation("Named Pipe IPC service started with pipe: {PipeName}", _pipeName);

                // Update rendezvous file (websocket portion may already exist; merge behavior done in helper by overwrite)
                await IpcRendezvousFile.TryUpdateAsync(token: _currentToken, webSocketPort: null, pipeName: _pipeName, epoch: _currentEpoch);
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
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error accepting named pipe connection");
                    await Task.Delay(500); // small backoff
                }
            }
        }

        private async Task HandlePipeConnection(NamedPipeServerStream server)
        {
            ClientContext? client = null;
            try
            {
                client = new ClientContext
                {
                    Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token),
                    TransportHandle = server
                };
                _clients[client.Id] = client;
                _logger.LogDebug("Pipe client {ClientId} connected", client.Id);

                // Minimal framing: length (int32 little endian) + utf8 json
                var reader = new BinaryReader(server, Encoding.UTF8, leaveOpen: true);
                var writer = new BinaryWriter(server, Encoding.UTF8, leaveOpen: true);

                while (server.IsConnected && !client.Cancellation!.IsCancellationRequested)
                {
                    // read length
                    var lenBytes = new byte[4];
                    int read = await server.ReadAsync(lenBytes, 0, 4, client.Cancellation.Token);
                    if (read == 0) break; // disconnected
                    if (read != 4) throw new IOException("Incomplete length prefix");
                    var length = BinaryPrimitives.ReadInt32LittleEndian(lenBytes);
                    if (length <= 0 || length > IpcConfig.NamedPipeMaxMessageBytes) break;

                    var payloadBytes = new byte[length];
                    int offset = 0;
                    while (offset < length)
                    {
                        var r = await server.ReadAsync(payloadBytes, offset, length - offset, client.Cancellation.Token);
                        if (r == 0) throw new IOException("Unexpected EOF");
                        offset += r;
                    }

                    var json = Encoding.UTF8.GetString(payloadBytes);
                    var message = JsonRpcMessage.FromJson(json);
                    if (message?.Method == "handshake")
                    {
                        await HandleHandshakeAsync(client, message, writer);
                        continue;
                    }

                    if (!_methodRegistry.TryGet(message?.Method ?? string.Empty, out var methodDef))
                    {
                        if (!message!.IsNotification)
                        {
                            await WriteResponseAsync(writer, JsonRpcMessage.MakeError(message.Id, -32601, "Method not found"));
                        }
                        continue;
                    }

                    if (methodDef.RequiresAuth && !client.IsAuthenticated)
                    {
                        if (!message!.IsNotification)
                        {
                            await WriteResponseAsync(writer, JsonRpcMessage.MakeError(message.Id, -32001, "Authentication required"));
                        }
                        continue;
                    }

                    if (!client.TryConsumeToken())
                    {
                        if (!message!.IsNotification)
                        {
                            await WriteResponseAsync(writer, JsonRpcMessage.MakeError(message.Id, -32003, "Rate limit exceeded"));
                        }
                        continue;
                    }

                    OnRequestReceived?.Invoke(client, message!);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipe connection error");
            }
            finally
            {
                if (client != null)
                {
                    _clients.TryRemove(client.Id, out _);
                    client.Dispose();
                }
                try { server.Dispose(); } catch { }
                _logger.LogDebug("Pipe client disconnected");
            }
        }

        private async Task HandleHandshakeAsync(ClientContext client, JsonRpcMessage message, BinaryWriter writer)
        {
            if (_tokenUsed)
            {
                if (!message.IsNotification)
                {
                    await WriteResponseAsync(writer, JsonRpcMessage.MakeError(message.Id, -32004, "Token already used"));
                }
                return;
            }

            if (message.Params?.TryGetProperty("token", out var tokenProp) != true)
            {
                await WriteResponseAsync(writer, JsonRpcMessage.MakeError(message.Id, -32602, "Missing token parameter"));
                return;
            }

            if (tokenProp.GetString() != _currentToken)
            {
                await WriteResponseAsync(writer, JsonRpcMessage.MakeError(message.Id, -32002, "Invalid token"));
                return;
            }

            client.IsAuthenticated = true;
            client.AuthEpoch = _currentEpoch;
            _tokenUsed = true;
            _currentToken = null;
            await IpcRendezvousFile.TryDeleteAsync();

            if (!message.IsNotification)
            {
                await WriteResponseAsync(writer, JsonRpcMessage.MakeResult(message.Id, new { status = "authenticated", epoch = _currentEpoch }));
            }

            _logger.LogInformation("Pipe client {ClientId} authenticated", client.Id);
        }

        private static async Task WriteResponseAsync(BinaryWriter writer, JsonRpcMessage response)
        {
            var json = response.ToJson();
            var bytes = Encoding.UTF8.GetBytes(json);
            var len = BitConverter.GetBytes(bytes.Length);
            writer.Write(len);
            writer.Write(bytes);
            writer.Flush();
            await Task.CompletedTask;
        }

        private void SendKeepalive(object? state)
        {
            // Pipe keepalive omitted for brevity
        }

        private void CleanupInactiveClients(object? state)
        {
            // Basic cleanup of disconnected pipe clients
            foreach (var kvp in _clients)
            {
                if (kvp.Value.Cancellation?.IsCancellationRequested == true)
                {
                    _clients.TryRemove(kvp.Key, out _);
                }
            }
        }

        public async Task SendResponseAsync(ClientContext client, JsonRpcMessage response)
        {
            // Named pipe responses handled directly in handler; not used here by coordinator currently.
            await Task.CompletedTask;
        }

        public async Task BroadcastAsync(JsonRpcMessage notification)
        {
            // Optional: implement broadcast for pipe clients later
            await Task.CompletedTask;
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _keepaliveTimer?.Dispose();
            _cleanupTimer?.Dispose();
            _cancellation.Dispose();
            foreach (var c in _clients.Values) c.Dispose();
        }
    }
}