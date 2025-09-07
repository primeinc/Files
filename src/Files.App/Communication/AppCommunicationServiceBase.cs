using System;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Files.App.Communication
{
    /// <summary>
    /// Base class providing common JSON-RPC IPC service functionality for multiple transports
    /// (WebSocket, Named Pipes, etc.). Handles:
    ///  - Token / epoch management
    ///  - Client registry & lifecycle
    ///  - Periodic keepalive pings (30s)
    ///  - Periodic stale client cleanup (60s interval, >5 min inactivity)
    ///  - Handshake ("handshake" method) authentication
    ///  - Rate limiting & basic JSON-RPC validation
    ///  - Unified request dispatch via OnRequestReceived
    /// Transport specific subclasses are only responsible for:
    ///  - Accepting connections & constructing ClientContext
    ///  - Running per-client send / receive loops
    ///  - Implementing raw send in <see cref="SendToClientAsync"/>
    /// </summary>
    public abstract class AppCommunicationServiceBase : IAppCommunicationService, IDisposable
    {
        // Dependencies
        protected readonly RpcMethodRegistry MethodRegistry; // shared method registry
        protected readonly ILogger Logger;

        // Auth / runtime identity
        protected string? CurrentToken { get; private set; }
        protected int CurrentEpoch { get; private set; }

        // State
        private bool _started;
        protected readonly ConcurrentDictionary<Guid, ClientContext> Clients = new();
        protected readonly CancellationTokenSource Cancellation = new();

        // Timers
        private readonly Timer _keepAliveTimer;
        private readonly Timer _cleanupTimer;

        // Events
        public event Func<ClientContext, JsonRpcMessage, Task>? OnRequestReceived;

        protected AppCommunicationServiceBase(RpcMethodRegistry methodRegistry, ILogger logger)
        {
            MethodRegistry = methodRegistry ?? throw new ArgumentNullException(nameof(methodRegistry));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _keepAliveTimer = new Timer(_ => SendKeepalive(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _cleanupTimer = new Timer(_ => CleanupInactiveClients(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        /// <summary>Starts the service (idempotent).</summary>
        public async Task StartAsync()
        {
            if (!ProtectedTokenStore.IsEnabled())
            {
                Logger.LogWarning("Remote control is not enabled, refusing to start {Service}", GetType().Name);
                return;
            }
            if (_started)
                return;

            try
            {
                CurrentToken = IpcRendezvousFile.GetOrCreateToken();
                CurrentEpoch = ProtectedTokenStore.GetEpoch();

                await StartTransportAsync();

                // Start timers AFTER transport to avoid ping before clients can connect
                _keepAliveTimer.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
                _cleanupTimer.Change(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
                _started = true;
                Logger.LogInformation("IPC transport {Service} started", GetType().Name);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed starting transport {Service}", GetType().Name);
                throw;
            }
        }

        /// <summary>Stops the service (idempotent).</summary>
        public async Task StopAsync()
        {
            if (!_started)
                return;
            try
            {
                Cancellation.Cancel();
                await StopTransportAsync();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Errors stopping {Service}", GetType().Name);
            }
            finally
            {
                foreach (var kv in Clients)
                {
                    kv.Value.Dispose();
                }
                Clients.Clear();
                _keepAliveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _cleanupTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _started = false;
            }
        }

        /// <summary>Process a raw already-parsed JSON-RPC message instance (transport calls this).</summary>
        protected async Task ProcessIncomingMessageAsync(ClientContext client, JsonRpcMessage? message)
        {
            if (message is null)
                return;

            client.LastSeenUtc = DateTime.UtcNow;

            // Basic validation
            if (!JsonRpcMessage.ValidJsonRpc(message) || JsonRpcMessage.IsInvalidRequest(message))
            {
                if (!message.IsNotification)
                    await EnqueueResponseAsync(client, JsonRpcMessage.MakeError(message.Id, -32600, "Invalid Request"));
                return;
            }

            // Handshake
            if (await HandleHandshakeAsync(client, message))
                return; // fully handled

            // Unknown method
            if (!MethodRegistry.TryGet(message.Method ?? string.Empty, out var methodDef))
            {
                if (!message.IsNotification)
                    await EnqueueResponseAsync(client, JsonRpcMessage.MakeError(message.Id, -32601, "Method not found"));
                return;
            }

            // Auth required
            if (methodDef.RequiresAuth && !client.IsAuthenticated)
            {
                if (!message.IsNotification)
                    await EnqueueResponseAsync(client, JsonRpcMessage.MakeError(message.Id, -32001, "Authentication required"));
                return;
            }

            // Rate limiting
            if (!client.TryConsumeToken())
            {
                if (!message.IsNotification)
                    await EnqueueResponseAsync(client, JsonRpcMessage.MakeError(message.Id, -32003, "Rate limit exceeded"));
                return;
            }

            // Notifications allowed?
            if (message.IsNotification && !methodDef.AllowNotifications)
            {
                Logger.LogDebug("Dropping unauthorized notification {Method} from {Client}", message.Method, client.Id);
                return;
            }

            // Dispatch
            try
            {
                await (OnRequestReceived?.Invoke(client, message) ?? Task.CompletedTask);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Handler error for method {Method}", message.Method);
            }
        }

        /// <summary>Handle handshake if applicable.</summary>
        private async Task<bool> HandleHandshakeAsync(ClientContext client, JsonRpcMessage message)
        {
            if (!string.Equals(message.Method, "handshake", StringComparison.Ordinal))
                return false;

            try
            {
                if (message.Params?.TryGetProperty("token", out var tokenProp) != true)
                {
                    await EnqueueResponseAsync(client, JsonRpcMessage.MakeError(message.Id, -32602, "Missing token parameter"));
                    return true;
                }
                if (tokenProp.GetString() != CurrentToken)
                {
                    await EnqueueResponseAsync(client, JsonRpcMessage.MakeError(message.Id, -32002, "Invalid token"));
                    return true;
                }

                client.IsAuthenticated = true;
                client.AuthEpoch = CurrentEpoch;
                if (message.Params?.TryGetProperty("clientInfo", out var clientInfo) == true)
                    client.ClientInfo = clientInfo.GetString();

                if (!message.IsNotification)
                {
                    await EnqueueResponseAsync(client, JsonRpcMessage.MakeResult(message.Id, new
                    {
                        status = "authenticated",
                        epoch = CurrentEpoch,
                        serverInfo = "Files IPC Server"
                    }));
                }
                Logger.LogInformation("Client {ClientId} authenticated (epoch {Epoch})", client.Id, CurrentEpoch);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Handshake failure for client {ClientId}", client.Id);
            }
            return true;
        }

        /// <summary>Queues a response (non-notification) for a client.</summary>
        private Task EnqueueResponseAsync(ClientContext client, JsonRpcMessage response)
        {
            if (response.IsNotification)
            {
                Logger.LogWarning("Attempted to queue notification as response");
                return Task.CompletedTask;
            }
            try
            {
                client.TryEnqueue(response.ToJson(), false, response.Method);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed enqueue response to {Client}", client.Id);
            }
            return Task.CompletedTask;
        }

        public Task SendResponseAsync(ClientContext client, JsonRpcMessage response) => EnqueueResponseAsync(client, response);

        public Task BroadcastAsync(JsonRpcMessage notification)
        {
            if (!notification.IsNotification)
            {
                Logger.LogWarning("Attempted to broadcast non-notification message");
                return Task.CompletedTask;
            }

            var json = notification.ToJson();
            var method = notification.Method;
            foreach (var c in Clients.Values)
            {
                if (!c.IsAuthenticated) continue;
                if (!c.TryConsumeToken()) continue; // protect from floods
                c.TryEnqueue(json, true, method);
            }
            return Task.CompletedTask;
        }

        private void SendKeepalive()
        {
            if (!_started || Cancellation.IsCancellationRequested)
                return;
            try
            {
                var notif = new JsonRpcMessage
                {
                    Method = "ping",
                    Params = JsonSerializer.SerializeToElement(new { timestamp = DateTime.UtcNow })
                };
                _ = BroadcastAsync(notif); // fire & forget
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Keepalive failure");
            }
        }

        private void CleanupInactiveClients()
        {
            if (!_started || Cancellation.IsCancellationRequested)
                return;
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(5);
            foreach (var kv in Clients)
            {
                var c = kv.Value;
                if (c.LastSeenUtc < cutoff || c.Cancellation?.IsCancellationRequested == true)
                {
                    if (Clients.TryRemove(kv.Key, out var removed))
                    {
                        try { removed.Dispose(); } catch { }
                        Logger.LogDebug("Removed stale client {ClientId}", kv.Key);
                    }
                }
            }
        }

        /// <summary>Registers a newly connected client. Caller starts its send loop.</summary>
        protected void RegisterClient(ClientContext client) => Clients[client.Id] = client;
        protected void UnregisterClient(ClientContext client)
        {
            if (Clients.TryRemove(client.Id, out var removed))
            {
                try { removed.Dispose(); } catch { }
            }
        }

        /// <summary>Dequeues payloads and invokes <see cref="SendToClientAsync"/>. Subclasses can reuse.</summary>
        protected async Task RunSendLoopAsync(ClientContext client)
        {
            try
            {
                while (!Cancellation.IsCancellationRequested && client.Cancellation?.IsCancellationRequested != true)
                {
                    if (client.TryDequeue(out var item))
                    {
                        await SendToClientAsync(client, item.payload);
                        client.DecreaseQueuedBytes(Encoding.UTF8.GetByteCount(item.payload));
                    }
                    else
                    {
                        await Task.Delay(10, Cancellation.Token);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Send loop exited for client {Client}", client.Id);
            }
        }

        /// <summary>Implement raw transport write for a textual JSON payload.</summary>
        protected abstract Task SendToClientAsync(ClientContext client, string payload);
        protected abstract Task StartTransportAsync();
        protected abstract Task StopTransportAsync();

        public void Dispose()
        {
            try { Cancellation.Cancel(); } catch { }
            _keepAliveTimer.Dispose();
            _cleanupTimer.Dispose();
            foreach (var c in Clients.Values) { try { c.Dispose(); } catch { } }
            Cancellation.Dispose();
        }
    }
}
