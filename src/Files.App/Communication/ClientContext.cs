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

		// Try enqueue with lossy policy; drops oldest notifications when queue is full
		public bool TryEnqueue(string payload, bool isNotification, string? method = null)
		{
			var bytes = Encoding.UTF8.GetByteCount(payload);
			var currentBytes = Interlocked.Read(ref _queuedBytes);
			
			// If adding this message would exceed capacity, try to make room
			if (currentBytes + bytes > MaxQueuedBytes)
			{
				// For notifications, we can drop them when queue is full (lossy behavior)
				if (isNotification)
				{
					// Try to drop some older notifications to make room
					var itemsToKeep = new List<(string payload, bool isNotification, string? method)>();
					var freedBytes = 0;
					var targetFreedBytes = bytes + (MaxQueuedBytes / 10); // Free a bit extra to avoid frequent cleanup
					
					// Drain queue and decide what to keep
					while (SendQueue.TryDequeue(out var item) && freedBytes < targetFreedBytes)
					{
						if (!item.isNotification)
						{
							// Always keep responses
							itemsToKeep.Add(item);
						}
						else
						{
							// Drop this notification to free space
							var itemBytes = Encoding.UTF8.GetByteCount(item.payload);
							Interlocked.Add(ref _queuedBytes, -itemBytes);
							freedBytes += itemBytes;
						}
					}
					
					// Re-queue the items we're keeping
					foreach (var item in itemsToKeep)
						SendQueue.Enqueue(item);
					
					// Check if we freed enough space
					if (Interlocked.Read(ref _queuedBytes) + bytes > MaxQueuedBytes)
					{
						// Still not enough space, drop this message
						return false;
					}
				}
				else
				{
					// For responses, never drop - just reject if queue is full
					return false;
				}
			}

			// Add the message to queue
			SendQueue.Enqueue((payload, isNotification, method));
			Interlocked.Add(ref _queuedBytes, bytes);
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