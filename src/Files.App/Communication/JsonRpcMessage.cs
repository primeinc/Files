using System.Text.Json;
using System.Text.Json.Serialization;

namespace Files.App.Communication
{
	// Strict JSON-RPC 2.0 model with helpers that preserve original id types and enforce result XOR error.
	public sealed record JsonRpcMessage
	{
		private const string JsonRpcVersion = "2.0";

		[JsonPropertyName("jsonrpc")]
		public string JsonRpc { get; init; } = JsonRpcVersion;

		[JsonPropertyName("id")]
		public JsonElement? Id { get; init; } // omitted => notification

		[JsonPropertyName("method")]
		public string? Method { get; init; }

		[JsonPropertyName("params")]
		public JsonElement? Params { get; init; }

		[JsonPropertyName("result")]
		public JsonElement? Result { get; init; }

		[JsonPropertyName("error")]
		public JsonElement? Error { get; init; }

		public bool IsNotification => Id is null || (Id.HasValue && Id.Value.ValueKind == JsonValueKind.Null);

		public static JsonRpcMessage? FromJson(string json)
		{
			try { return JsonSerializer.Deserialize<JsonRpcMessage>(json); }
			catch { return null; }
		}

		public string ToJson() => JsonSerializer.Serialize(this);

		public static JsonRpcMessage MakeError(JsonElement? id, int code, string message)
		{
			var errObj = new { code, message };
			var doc = JsonSerializer.SerializeToElement(errObj);
			return new JsonRpcMessage { Id = id, Error = doc };
		}

		public static JsonRpcMessage MakeResult(JsonElement? id, object result)
		{
			var doc = JsonSerializer.SerializeToElement(result);
			return new JsonRpcMessage { Id = id, Result = doc };
		}

		public static bool ValidJsonRpc(JsonRpcMessage? msg) => msg is not null && msg.JsonRpc == JsonRpcVersion;

		// Validate that incoming message is a legal JSON-RPC request/notification/response shape
		public static bool IsInvalidRequest(JsonRpcMessage m)
		{
			var hasMethod = !string.IsNullOrEmpty(m.Method);
			var hasResult = m.Result is not null && m.Result.Value.ValueKind != JsonValueKind.Undefined;
			var hasError = m.Error is not null && m.Error.Value.ValueKind != JsonValueKind.Undefined;

			// result and error are mutually exclusive
			if (hasResult && hasError) 
				return true;

			// request or notification: method present; NO result/error
			if (hasMethod && (hasResult || hasError)) 
				return true;

			// response: no method; need exactly one of result or error
			if (!hasMethod && !(hasResult ^ hasError)) 
				return true;

			return false;
		}
	}
}