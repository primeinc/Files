using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.IO; // added for BinaryWriter

namespace Files.App.Communication
{
	// Per-client state with token-bucket, dual-priority queues and LastSeenUtc tracked.
	// Optimized for performance: O(1) dequeue, intelligent notification coalescing
	public sealed class ClientContext : IDisposable
	{
		// Fields
		private readonly object _rateLock = new();
		// Separate queues for responses (high priority) and notifications (low priority)
		private readonly ConcurrentQueue<(string payload, string? method)> _responseQueue = new();
		private readonly ConcurrentQueue<(string payload, string method)> _notificationQueue = new();
		// Track method counts for efficient duplicate dropping
		private readonly ConcurrentDictionary<string, int> _notificationMethodCounts = new(StringComparer.OrdinalIgnoreCase);
		private readonly SemaphoreSlim _messageAvailable = new(0); // Signal when messages are available
		private long _queuedBytes = 0;
		private int _tokens;
		private DateTime _lastRefill;
		private bool _disposed;

		// Added: lock for pipe writer operations
		internal object? PipeWriteLock { get; set; }

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

		// Async wait for messages to become available
		internal async Task<(string payload, bool isNotification, string? method)> DequeueAsync(CancellationToken cancellationToken)
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				await _messageAvailable.WaitAsync(cancellationToken);
				if (TryDequeueInternal(out var item))
					return item;
			}
			throw new OperationCanceledException();
		}

		// Efficient dequeue: responses first (high priority), then notifications (low priority)
		// O(1) operation with no scanning required
		internal bool TryDequeue(out (string payload, bool isNotification, string? method) item)
		{
			var result = TryDequeueInternal(out item);
			if (!result && _messageAvailable.CurrentCount > 0)
			{
				// Drain any excess semaphore count to prevent buildup
				while (_messageAvailable.CurrentCount > 0)
					_messageAvailable.Wait(0);
			}
			return result;
		}

		private bool TryDequeueInternal(out (string payload, bool isNotification, string? method) item)
		{
			// Always dequeue responses first (high priority)
			if (_responseQueue.TryDequeue(out var response))
			{
				var bytes = System.Text.Encoding.UTF8.GetByteCount(response.payload);
				System.Threading.Interlocked.Add(ref _queuedBytes, -bytes);
				item = (response.payload, false, response.method);
				return true;
			}
			
			// Then dequeue notifications (low priority)
			if (_notificationQueue.TryDequeue(out var notification))
			{
				var bytes = System.Text.Encoding.UTF8.GetByteCount(notification.payload);
				System.Threading.Interlocked.Add(ref _queuedBytes, -bytes);
				
				// Decrement method count
				if (_notificationMethodCounts.TryGetValue(notification.method, out var count))
				{
					if (count <= 1)
						_notificationMethodCounts.TryRemove(notification.method, out _);
					else
						_notificationMethodCounts.TryUpdate(notification.method, count - 1, count);
				}
				
				item = (notification.payload, true, notification.method);
				return true;
			}
			
			item = default;
			return false;
		}

		// Added: BinaryWriter for named pipe responses/notifications
		public BinaryWriter? PipeWriter { get; set; }

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
			lock (_rateLock)
			{
				RefillTokens();
				if (_tokens > 0)
				{
					_tokens--;
					return true;
				}
				return false;
			}
		}

		// Optimized TryEnqueue with dual-priority queues
		// Responses always get through, notifications use intelligent coalescing
		public bool TryEnqueue(string payload, bool isNotification, string? method = null)
		{
			var bytes = System.Text.Encoding.UTF8.GetByteCount(payload);
			
			// Responses always get enqueued (critical for protocol)
			if (!isNotification)
			{
				// Make room if needed by dropping notifications
				while (System.Threading.Interlocked.Read(ref _queuedBytes) + bytes > MaxQueuedBytes)
				{
					if (!DropOldestNotification())
						break; // No more notifications to drop
				}
				
				System.Threading.Interlocked.Add(ref _queuedBytes, bytes);
				_responseQueue.Enqueue((payload, method));
				_messageAvailable.Release(); // Signal message available
				return true;
			}

			// For notifications: check if we have room
			var newVal = System.Threading.Interlocked.Add(ref _queuedBytes, bytes);
			if (newVal <= MaxQueuedBytes)
			{
				// Fast path: under limit, just enqueue
				if (method != null)
				{
					_notificationQueue.Enqueue((payload, method));
					_notificationMethodCounts.AddOrUpdate(method, 1, (_, count) => count + 1);
					_messageAvailable.Release(); // Signal message available
				}
				return true;
			}

			// Revert the byte count
			System.Threading.Interlocked.Add(ref _queuedBytes, -bytes);

			// Try intelligent dropping for notifications
			if (method != null)
			{
				// If we have duplicates of this method, drop the oldest one (coalescing)
				if (_notificationMethodCounts.TryGetValue(method, out var existingCount) && existingCount > 0)
				{
					// This is O(n) but n is bounded and typically small
					if (DropOldestNotificationOfMethod(method))
					{
						// Now we should have room, enqueue the new notification
						System.Threading.Interlocked.Add(ref _queuedBytes, bytes);
						_notificationQueue.Enqueue((payload, method));
						_notificationMethodCounts.AddOrUpdate(method, 1, (_, c) => c + 1);
						_messageAvailable.Release(); // Signal message available
						return true;
					}
				}
				
				// No duplicate to drop, try dropping any notification
				if (DropOldestNotification())
				{
					System.Threading.Interlocked.Add(ref _queuedBytes, bytes);
					_notificationQueue.Enqueue((payload, method));
					_notificationMethodCounts.AddOrUpdate(method, 1, (_, c) => c + 1);
					_messageAvailable.Release(); // Signal message available
					return true;
				}
			}

			// Can't make room
			return false;
		}

		// Helper: Drop oldest notification of specific method
		// NOTE: This method has O(n) complexity where n is the queue size, as it must
		// rebuild the queue to remove an item from the middle. This is intentionally
		// accepted because:
		// 1. ConcurrentQueue provides thread-safety which is critical for our multi-threaded IPC
		// 2. This operation only occurs when the queue is full (rare in normal operation)
		// 3. Queue size is bounded by MaxQueuedBytes, keeping n relatively small
		// 4. Alternative data structures (e.g., LinkedList) would require complex synchronization
		private bool DropOldestNotificationOfMethod(string targetMethod)
		{
			var tempQueue = new System.Collections.Generic.List<(string payload, string method)>();
			var dropped = false;
			
			while (_notificationQueue.TryDequeue(out var item))
			{
				if (!dropped && item.method.Equals(targetMethod, StringComparison.OrdinalIgnoreCase))
				{
					// Drop this one
					var droppedBytes = System.Text.Encoding.UTF8.GetByteCount(item.payload);
					System.Threading.Interlocked.Add(ref _queuedBytes, -droppedBytes);
					_notificationMethodCounts.AddOrUpdate(targetMethod, 0, (_, c) => Math.Max(0, c - 1));
					dropped = true;
				}
				else
				{
					tempQueue.Add(item);
				}
			}
			
			// Re-enqueue the kept items
			foreach (var item in tempQueue)
				_notificationQueue.Enqueue(item);
			
			return dropped;
		}

		// Helper: Drop any oldest notification
		private bool DropOldestNotification()
		{
			if (_notificationQueue.TryDequeue(out var dropped))
			{
				var droppedBytes = System.Text.Encoding.UTF8.GetByteCount(dropped.payload);
				System.Threading.Interlocked.Add(ref _queuedBytes, -droppedBytes);
				
				// Update method count
				if (_notificationMethodCounts.TryGetValue(dropped.method, out var count))
				{
					if (count <= 1)
						_notificationMethodCounts.TryRemove(dropped.method, out _);
					else
						_notificationMethodCounts.TryUpdate(dropped.method, count - 1, count);
				}
				return true;
			}
			return false;
		}

		// Internal methods
		internal void DecreaseQueuedBytes(int sentBytes) => System.Threading.Interlocked.Add(ref _queuedBytes, -sentBytes);

		// Dispose
		public void Dispose()
		{
			if (_disposed)
				return;

			_disposed = true;
			Cancellation?.Cancel();
			Cancellation?.Dispose();

			if (WebSocket?.State == WebSocketState.Open)
			{
				try
				{
					WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None).Wait(1000);
				}
				catch { }
			}

			WebSocket?.Dispose();
			PipeWriter?.Dispose();
			_messageAvailable?.Dispose();
		}
	}
}