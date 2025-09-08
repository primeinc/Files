using Files.App.Data.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Files.App.Communication
{
    /// <summary>
    /// Wraps IShellPage to provide INavigationStateProvider implementation.
    /// </summary>
    public sealed class NavigationStateFromShell : INavigationStateProvider, IDisposable
    {
        private readonly IShellPage _page;
        public event EventHandler? StateChanged;

        public NavigationStateFromShell(IShellPage page)
        {
            _page = page ?? throw new ArgumentNullException(nameof(page));

            // Subscribe to state change events
            if (_page.ShellViewModel != null)
            {
                _page.ShellViewModel.WorkingDirectoryModified += OnWorkingDirectoryModified;
            }
            
            // Note: NavigationToolbar.Navigated would be ideal but we need to check if it exists
            _page.PropertyChanged += OnPagePropertyChanged;
        }

        public string CurrentPath => _page.ShellViewModel?.WorkingDirectory ?? string.Empty;

        public bool CanGoBack => _page.CanNavigateBackward;

        public bool CanGoForward => _page.CanNavigateForward;

        public async Task NavigateToAsync(string path, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be empty", nameof(path));

            if (ct.IsCancellationRequested)
                return;

            _page.NavigateToPath(path);
            await Task.CompletedTask;
        }

        private void OnWorkingDirectoryModified(object? sender, WorkingDirectoryModifiedEventArgs e)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnPagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IShellPage.CanNavigateBackward) ||
                e.PropertyName == nameof(IShellPage.CanNavigateForward))
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            if (_page.ShellViewModel != null)
            {
                _page.ShellViewModel.WorkingDirectoryModified -= OnWorkingDirectoryModified;
            }
            _page.PropertyChanged -= OnPagePropertyChanged;
        }
    }
}