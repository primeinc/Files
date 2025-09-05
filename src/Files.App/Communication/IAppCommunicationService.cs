using System;
using System.Threading.Tasks;

namespace Files.App.Communication
{
	/// <summary>
	/// Represents a communication service for handling JSON-RPC messages between clients and the application.
	/// Implementations provide transport-specific functionality (WebSocket, Named Pipe, etc.)
	/// </summary>
	public interface IAppCommunicationService
	{
		/// <summary>
		/// Occurs when a JSON-RPC request is received from a client.
		/// </summary>
		event Func<ClientContext, JsonRpcMessage, Task>? OnRequestReceived;

		/// <summary>
		/// Starts the communication service and begins listening for client connections.
		/// </summary>
		/// <returns>A task that represents the asynchronous start operation.</returns>
		Task StartAsync();

		/// <summary>
		/// Stops the communication service and closes all client connections.
		/// </summary>
		/// <returns>A task that represents the asynchronous stop operation.</returns>
		Task StopAsync();

		/// <summary>
		/// Sends a JSON-RPC response message to a specific client.
		/// </summary>
		/// <param name="client">The client context to send the response to.</param>
		/// <param name="response">The JSON-RPC response message to send.</param>
		/// <returns>A task that represents the asynchronous send operation.</returns>
		Task SendResponseAsync(ClientContext client, JsonRpcMessage response);

		/// <summary>
		/// Broadcasts a JSON-RPC notification message to all connected clients.
		/// </summary>
		/// <param name="notification">The JSON-RPC notification message to broadcast.</param>
		/// <returns>A task that represents the asynchronous broadcast operation.</returns>
		Task BroadcastAsync(JsonRpcMessage notification);
	}
}