using System;

namespace Files.App.Communication
{
    /// <summary>
    /// Exception for JSON-RPC errors with proper error codes.
    /// </summary>
    public sealed class JsonRpcException : Exception
    {
        public int Code { get; }

        public JsonRpcException(int code, string message) : base(message)
        {
            Code = code;
        }

        public JsonRpcException(int code, string message, Exception innerException) 
            : base(message, innerException)
        {
            Code = code;
        }

        // Standard JSON-RPC error codes
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32000;
    }
}