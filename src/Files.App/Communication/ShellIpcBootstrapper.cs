using Files.App.Data.Contracts;
using Files.App.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System;

namespace Files.App.Communication
{
    /// <summary>
    /// Bootstraps IPC adapter for a shell instance and manages its lifecycle.
    /// </summary>
    public sealed class ShellIpcBootstrapper : IDisposable
    {
        public Guid ShellId { get; } = Guid.NewGuid();
        
        private readonly IIpcShellRegistry _registry;
        private readonly IShellPage _page;
        private readonly NavigationStateFromShell _nav;
        private readonly ShellIpcAdapter _adapter;
        private readonly uint _appWindowId;
        private readonly Guid _tabId;
        private readonly ILogger<ShellIpcBootstrapper> _logger;
        private bool _disposed;

        public ShellIpcBootstrapper(
            IIpcShellRegistry registry,
            IShellPage page,
            uint appWindowId,
            Guid tabId,
            IAppCommunicationService commService,
            ActionRegistry actionRegistry,
            RpcMethodRegistry methodRegistry,
            DispatcherQueue dispatcherQueue,
            ILogger<ShellIpcBootstrapper> logger,
            ILogger<ShellIpcAdapter> adapterLogger)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _page = page ?? throw new ArgumentNullException(nameof(page));
            _appWindowId = appWindowId;
            _tabId = tabId;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            try
            {
                // Create the navigation state wrapper
                _nav = new NavigationStateFromShell(page);

                // Create the adapter
                _adapter = new ShellIpcAdapter(
                    page.ShellViewModel,
                    commService,
                    actionRegistry,
                    methodRegistry,
                    dispatcherQueue,
                    adapterLogger,
                    _nav);

                // Register with the registry
                var descriptor = new IpcShellDescriptor(ShellId, _appWindowId, _tabId, _adapter, false);
                _registry.Register(descriptor);

                // TODO: Hook up focus events to track active shell when available
                // For now, mark as active immediately
                _registry.SetActive(ShellId);

                _logger.LogInformation("Bootstrapped IPC for shell {ShellId} in window {WindowId}, tab {TabId}", 
                    ShellId, _appWindowId, _tabId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to bootstrap IPC for shell");
                Dispose();
                throw;
            }
        }

        // TODO: Implement when IsCurrentPaneChanged event is available
        // private void OnIsCurrentPaneChanged(object? sender, EventArgs e)
        // {
        //     if (_page.IsCurrentPane)
        //     {
        //         _registry.SetActive(ShellId);
        //         _logger.LogDebug("Shell {ShellId} became active", ShellId);
        //     }
        // }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                // TODO: Unhook when event is available
                // _page.IsCurrentPaneChanged -= OnIsCurrentPaneChanged;

                // Unregister from registry
                _registry.Unregister(ShellId);

                // Dispose the navigation wrapper
                _nav?.Dispose();

                _logger.LogInformation("Disposed IPC bootstrapper for shell {ShellId}", ShellId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during IPC bootstrapper disposal for shell {ShellId}", ShellId);
            }
        }
    }
}