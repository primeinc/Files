// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace Files.App.ViewModels
{
	/// <summary>
	/// Default navigation state provider backed by an IShellPage.
	/// </summary>
	public sealed class ShellNavigationStateProvider : INavigationStateProvider, IDisposable
	{
		private readonly IShellPage _shellPage;
		private readonly ILogger<ShellNavigationStateProvider> _logger;

		public ShellNavigationStateProvider(IShellPage shellPage, ILogger<ShellNavigationStateProvider> logger)
		{
			_shellPage = shellPage ?? throw new ArgumentNullException(nameof(shellPage));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));

			_shellPage.ShellViewModel.WorkingDirectoryModified += OnWorkingDirectoryModified;
			_shellPage.ToolbarViewModel.PropertyChanged += OnToolbarChanged;
		}

		public string? CurrentPath => _shellPage.ShellViewModel.WorkingDirectory;
		public bool CanGoBack => _shellPage.ToolbarViewModel.CanGoBack;
		public bool CanGoForward => _shellPage.ToolbarViewModel.CanGoForward;

		public event EventHandler? StateChanged;

		private void OnWorkingDirectoryModified(object? sender, WorkingDirectoryModifiedEventArgs e)
		{
			StateChanged?.Invoke(this, EventArgs.Empty);
		}

		private void OnToolbarChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(_shellPage.ToolbarViewModel.CanGoBack) or nameof(_shellPage.ToolbarViewModel.CanGoForward))
			{
				StateChanged?.Invoke(this, EventArgs.Empty);
			}
		}

		public async Task NavigateToAsync(string path, CancellationToken ct = default)
		{
			try
			{
				await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() =>
				{
					_shellPage.NavigateToPath(path);
				});
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to navigate to {Path}", path);
				throw;
			}
		}

		public void Dispose()
		{
			_shellPage.ToolbarViewModel.PropertyChanged -= OnToolbarChanged;
			_shellPage.ShellViewModel.WorkingDirectoryModified -= OnWorkingDirectoryModified;
		}
	}
}
