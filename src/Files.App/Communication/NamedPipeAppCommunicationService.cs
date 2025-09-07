using Microsoft.Extensions.Logging;
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Files.App.Communication
{
    public sealed class NamedPipeAppCommunicationService : AppCommunicationServiceBase
    {
        private string? _pipeName;
        private bool _transportStarted;
        private Task? _acceptTask;

        public NamedPipeAppCommunicationService(
            RpcMethodRegistry methodRegistry,
            ILogger<NamedPipeAppCommunicationService> logger)
            : base(methodRegistry, logger)
        { }

        protected override async Task StartTransportAsync()
        {
            _pipeName = $"FilesAppPipe_{Environment.UserName}_{Guid.NewGuid():N}";
            _transportStarted = true;
            _acceptTask = Task.Run(AcceptConnectionsAsync, Cancellation.Token);
            await IpcRendezvousFile.UpdateAsync(pipeName: _pipeName, epoch: CurrentEpoch);
        }

        protected override async Task StopTransportAsync()
        {
            if (!_transportStarted) return;
            try
            {
                if (_acceptTask != null)
                    await _acceptTask; // wait graceful exit
            }
            catch { }
            finally
            {
                _transportStarted = false;
            }
        }

        private PipeSecurity CreatePipeSecurity()
        {
            var pipeSecurity = new PipeSecurity();
            var currentUser = WindowsIdentity.GetCurrent();
            if (currentUser?.User != null)
            {
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    currentUser.User,
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));
            }
            return pipeSecurity;
        }

        private async Task AcceptConnectionsAsync()
        {
            while (!Cancellation.IsCancellationRequested)
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

                    Logger.LogDebug("Waiting for named pipe connection...");
                    await server.WaitForConnectionAsync(Cancellation.Token);
                    _ = Task.Run(() => HandleConnectionAsync(server), Cancellation.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error accepting named pipe connection");
                    await Task.Delay(250, Cancellation.Token);
                }
            }
        }

        private async Task HandleConnectionAsync(NamedPipeServerStream server)
        {
            ClientContext? client = null;
            try
            {
                client = new ClientContext
                {
                    Cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation.Token),
                    TransportHandle = server,
                    PipeWriter = new BinaryWriter(server, Encoding.UTF8, leaveOpen: true),
                    PipeWriteLock = new object()
                };
                RegisterClient(client);
                Logger.LogDebug("Pipe client {ClientId} connected", client.Id);

                // Dual loops
                _ = Task.Run(() => RunSendLoopAsync(client), client.Cancellation.Token); // send loop
                await RunReceiveLoopAsync(client, server); // receive loop (exits on disconnect)
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Pipe connection handler error");
            }
            finally
            {
                if (client != null)
                {
                    UnregisterClient(client);
                    Logger.LogDebug("Pipe client {ClientId} disconnected", client.Id);
                }
                try { server.Dispose(); } catch { }
            }
        }

        private async Task RunReceiveLoopAsync(ClientContext client, NamedPipeServerStream server)
        {
            var reader = new BinaryReader(server, Encoding.UTF8, leaveOpen: true);
            try
            {
                while (server.IsConnected && !client.Cancellation!.IsCancellationRequested)
                {
                    var lenBytes = new byte[4];
                    int read = await server.ReadAsync(lenBytes, 0, 4, client.Cancellation.Token);
                    if (read == 0) break; // disconnect
                    if (read != 4) throw new IOException("Incomplete length prefix");

                    var length = BinaryPrimitives.ReadInt32LittleEndian(lenBytes);
                    if (length <= 0 || length > IpcConfig.NamedPipeMaxMessageBytes)
                        break; // invalid / over limit

                    var payload = new byte[length];
                    int offset = 0;
                    while (offset < length)
                    {
                        var r = await server.ReadAsync(payload, offset, length - offset, client.Cancellation.Token);
                        if (r == 0) throw new IOException("Unexpected EOF");
                        offset += r;
                    }

                    var json = Encoding.UTF8.GetString(payload);
                    var msg = JsonRpcMessage.FromJson(json);
                    await ProcessIncomingMessageAsync(client, msg);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Receive loop error for pipe client {ClientId}", client.Id);
            }
        }

        protected override Task SendToClientAsync(ClientContext client, string payload)
        {
            // Frame: length prefix + UTF8 bytes
            if (client.PipeWriter is null || client.PipeWriteLock is null) return Task.CompletedTask;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(payload);
                var len = new byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(len, bytes.Length);
                lock (client.PipeWriteLock)
                {
                    client.PipeWriter.Write(len);
                    client.PipeWriter.Write(bytes);
                    client.PipeWriter.Flush();
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Pipe send error to {ClientId}", client.Id);
            }
            return Task.CompletedTask;
        }
    }
}