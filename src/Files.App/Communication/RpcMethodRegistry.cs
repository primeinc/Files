using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Files.App.Communication
{
	public sealed class RpcMethod
	{
		public string Name { get; init; } = string.Empty;

		public int? MaxPayloadBytes { get; init; } // optional cap per method

		public bool RequiresAuth { get; init; } = true;

		public bool AllowNotifications { get; init; } = true;

		public Func<ClientContext, JsonRpcMessage, bool>? AuthorizationPolicy { get; init; } // additional checks
	}

	public sealed class RpcMethodRegistry
	{
		private readonly ConcurrentDictionary<string, RpcMethod> _methods = new();

		public RpcMethodRegistry()
		{
			Register(new RpcMethod { Name = "handshake", RequiresAuth = false, AllowNotifications = false });
			Register(new RpcMethod { Name = "getState", RequiresAuth = true, AllowNotifications = false });
			Register(new RpcMethod { Name = "listActions", RequiresAuth = true, AllowNotifications = false });
			Register(new RpcMethod { Name = "getMetadata", RequiresAuth = true, AllowNotifications = false, MaxPayloadBytes = 2 * 1024 * 1024 });
			Register(new RpcMethod { Name = "navigate", RequiresAuth = true, AllowNotifications = false });
			Register(new RpcMethod { Name = "executeAction", RequiresAuth = true, AllowNotifications = false });
		}

		public void Register(RpcMethod method) => _methods[method.Name] = method;

		public bool TryGet(string name, out RpcMethod method) => _methods.TryGetValue(name, out method);

		public IEnumerable<RpcMethod> List() => _methods.Values;
	}
}