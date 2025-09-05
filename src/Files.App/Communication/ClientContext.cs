using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

namespace Files.App.Communication
{
	// Per-client state with token-bucket, lossy enqueue and LastSeenUtc tracked.
	public sealed class ClientContext : IDisposable
	{
		// readonly fields
		private readonly object _rateLock = new();
		private readonly ConcurrentQueue<(string payload, bool isNotification, string? method)> _sendQueue = new();

		// Fields
		private long _queuedBytes = 0;
		private int _tokens;
		private DateTime _lastRefill;

		// _disposed field
		private bool _disposed;

		// Properties  
		public Guid Id { get; } = Guid.NewGuid();

		public string? ClientInfo { get; set; }

		public bool IsAuthenticated { get; set; }

		public int AuthEpoch { get; set; } = 0; // set at handshake

		public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

		public long MaxQueuedBytes { get; set; } = IpcConfig.PerClientQueueCapBytes;

		public CancellationTokenSource? Cancellation { get; set; }

		public WebSocket? WebSocket { get; set; }

		public object? TransportHandle { get; set; } // can store session id, pipe name, etc.

		internal ConcurrentQueue<(string payload, bool isNotification, string? method)> SendQueue => _sendQueue;

		// Constructor
		public ClientContext()
		{
			_tokens = IpcConfig.RateLimitBurst;
			_lastRefill = DateTime.UtcNow;
		}

		// Public methods
		public void RefillTokens()
		{
			lock (_rateLock)
			{
				var now = DateTime.UtcNow;
				var delta = (now - _lastRefill).TotalSeconds;
				if (delta <= 0) 
					return;

				var add = (int)(delta * IpcConfig.RateLimitPerSecond);
				if (add > 0)
				{
					_tokens = Math.Min(IpcConfig.RateLimitBurst, _tokens + add);
					_lastRefill = now;
				}
			}
		}

		public bool TryConsumeToken()
		{
			RefillTokens();
			lock (_rateLock)
			{
				if (_tokens <= 0) 
					return false;

				_tokens--;
				return true;
			}
		}

		// Try enqueue with lossy policy; drops oldest notifications of the same method first when needed.
		public bool TryEnqueue(string payload, bool isNotification, string? method = null)
		{
			var bytes = Encoding.UTF8.GetByteCount(payload);
			var newVal = Interlocked.Add(ref _queuedBytes, bytes);
			if (newVal > MaxQueuedBytes)
			{
				// attempt to free by dropping oldest notifications (prefer same-method)
				int freed = 0;
				var initialQueue = new List<(string payload, bool isNotification, string? method)>();
				while (SendQueue.TryDequeue(out var old))
				{
					if (!old.isNotification)
					{
						initialQueue.Add(old); // keep responses
					}
					else if (old.method != null && method != null && old.method.Equals(method, StringComparison.OrdinalIgnoreCase) && freed == 0)
					{
						// drop one older of same method
						var b = Encoding.UTF8.GetByteCount(old.payload);
						Interlocked.Add(ref _queuedBytes, -b);
						freed += b;
						break;
					}
					else
					{
						// for fairness, try dropping other notifications as well
						var b = Encoding.UTF8.GetByteCount(old.payload);
						Interlocked.Add(ref _queuedBytes, -b);
						freed += b;
						if (Interlocked.Read(ref _queuedBytes) <= MaxQueuedBytes) 
							break;
					}
				}

				// push back preserved responses
				foreach (var item in initialQueue) 
					SendQueue.Enqueue(item);

				newVal = Interlocked.Read(ref _queuedBytes);
				if (newVal + bytes > MaxQueuedBytes)
				{
					// still cannot enqueue
					return false;
				}
			}

			SendQueue.Enqueue((payload, isNotification, method));
			return true;
		}

		// Internal methods
		internal void DecreaseQueuedBytes(int sentBytes) => Interlocked.Add(ref _queuedBytes, -sentBytes);

		// Dispose
		public void Dispose()
		{
			if (_disposed)
				return;

			try { Cancellation?.Cancel(); } catch { }
			try { WebSocket?.Dispose(); } catch { }
			_disposed = true;
		}
	}
}