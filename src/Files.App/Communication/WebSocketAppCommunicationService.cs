using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Files.App.Communication
{
    public sealed class WebSocketAppCommunicationService : AppCommunicationServiceBase
    {
        private readonly HttpListener _httpListener;
        private bool _transportStarted;
        private int? _port; // chosen port

        public WebSocketAppCommunicationService(
            RpcMethodRegistry methodRegistry,
            ILogger<WebSocketAppCommunicationService> logger)
            : base(methodRegistry, logger)
        {
            _httpListener = new HttpListener();
        }

        protected override async Task StartTransportAsync()
        {
            // Bind port & start listener
            _port = BindAvailablePort();
            _httpListener.Start();
            _transportStarted = true;
            _ = Task.Run(AcceptConnectionsAsync, Cancellation.Token);
            await IpcRendezvousFile.UpdateAsync(webSocketPort: _port, epoch: CurrentEpoch);
        }

        protected override Task StopTransportAsync()
        {
            if (_transportStarted)
            {
                try { _httpListener.Stop(); } catch { }
                try { _httpListener.Close(); } catch { }
                _transportStarted = false;
            }
            return Task.CompletedTask;
        }

        private int BindAvailablePort()
        {
            int[] preferred = { 52345 };
            foreach (var p in preferred)
            {
                if (TryBindPort(p)) return p;
            }
            for (int p = 40000; p < 40100; p++)
            {
                if (TryBindPort(p)) return p;
            }
            throw new InvalidOperationException("No available port for WebSocket IPC");
        }

        private bool TryBindPort(int port)
        {
            try
            {
                _httpListener.Prefixes.Clear();
                _httpListener.Prefixes.Add($"http://127.0.0.1:{port}/");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Port {Port} unavailable", port);
                return false;
            }
        }

        private async Task AcceptConnectionsAsync()
        {
            while (!Cancellation.IsCancellationRequested)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync();
                    if (context.Request.IsWebSocketRequest)
                        _ = Task.Run(() => HandleWebSocketConnection(context), Cancellation.Token);
                    else
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                    }
                }
                catch (HttpListenerException) when (Cancellation.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error accepting WebSocket connection");
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
                    Cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation.Token)
                };
                RegisterClient(client);
                Logger.LogDebug("WebSocket client {ClientId} connected", client.Id);

                // Start send loop
                _ = Task.Run(() => RunSendLoopAsync(client), client.Cancellation.Token);
                await ClientReceiveLoopAsync(client);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in WebSocket connection handler");
            }
            finally
            {
                if (client != null)
                {
                    UnregisterClient(client);
                    Logger.LogDebug("WebSocket client {ClientId} disconnected", client.Id);
                }
            }
        }

        private async Task ClientReceiveLoopAsync(ClientContext client)
        {
            var buffer = new byte[4096];
            var builder = new StringBuilder();
            var received = 0;
            try
            {
                while (client.WebSocket?.State == WebSocketState.Open && !client.Cancellation!.IsCancellationRequested)
                {
                    var result = await client.WebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), client.Cancellation.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                    if (result.MessageType != WebSocketMessageType.Text)
                        continue;

                    received += result.Count;
                    if (received > IpcConfig.WebSocketMaxMessageBytes)
                    {
                        Logger.LogWarning("Client {ClientId} exceeded max message size, disconnecting", client.Id);
                        break;
                    }
                    builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (result.EndOfMessage)
                    {
                        var text = builder.ToString();
                        builder.Clear();
                        received = 0;
                        var msg = JsonRpcMessage.FromJson(text);
                        await ProcessIncomingMessageAsync(client, msg);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException ex)
            {
                Logger.LogDebug("WebSocket error {Client}: {Message}", client.Id, ex.Message);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Receive loop error {Client}", client.Id);
            }
        }

        protected override async Task SendToClientAsync(ClientContext client, string payload)
        {
            if (client.WebSocket is not { State: WebSocketState.Open })
                return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(payload);
                await client.WebSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, client.Cancellation!.Token);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Send error to client {Client}", client.Id);
            }
        }
    }
}