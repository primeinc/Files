using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace Files.App.Communication
{
	// Ensures all UI-affecting operations are serialized on the dispatcher thread
	public sealed class UIOperationQueue
	{
		// readonly fields
		private readonly DispatcherQueue _dispatcher;

		// Constructor
		public UIOperationQueue(DispatcherQueue dispatcher)
		{
			_dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
		}

		// Public methods
		public Task EnqueueAsync(Func<Task> operation)
		{
			var tcs = new TaskCompletionSource<object?>();

			_dispatcher.TryEnqueue(async () =>
			{
				try
				{
					await operation().ConfigureAwait(false);
					tcs.SetResult(null);
				}
				catch (Exception ex)
				{
					tcs.SetException(ex);
				}
			});

			return tcs.Task;
		}
	}
}