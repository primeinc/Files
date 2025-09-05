using System;
using System.Threading.Tasks;

namespace Files.App.Communication
{
    public interface IAppCommunicationService
    {
        event Func<ClientContext, JsonRpcMessage, Task>? OnRequestReceived;
        
        Task StartAsync();
        Task StopAsync();
        Task SendResponseAsync(ClientContext client, JsonRpcMessage response);
        Task BroadcastAsync(JsonRpcMessage notification);
    }
}