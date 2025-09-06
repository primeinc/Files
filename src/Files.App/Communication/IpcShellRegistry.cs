using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Files.App.Communication
{
    /// <summary>
    /// Thread-safe registry implementation for shell IPC adapters.
    /// </summary>
    public sealed class IpcShellRegistry : IIpcShellRegistry
    {
        private readonly ConcurrentDictionary<Guid, IpcShellDescriptor> _shells = new();
        private readonly ILogger<IpcShellRegistry> _logger;

        public IpcShellRegistry(ILogger<IpcShellRegistry> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Register(IpcShellDescriptor descriptor)
        {
            if (descriptor == null)
                throw new ArgumentNullException(nameof(descriptor));

            if (_shells.TryAdd(descriptor.ShellId, descriptor))
            {
                _logger.LogInformation("Registered shell {ShellId} for window {WindowId}, tab {TabId}", 
                    descriptor.ShellId, descriptor.AppWindowId, descriptor.TabId);
            }
            else
            {
                _logger.LogWarning("Failed to register duplicate shell {ShellId}", descriptor.ShellId);
            }
        }

        public void Unregister(Guid shellId)
        {
            if (_shells.TryRemove(shellId, out var descriptor))
            {
                _logger.LogInformation("Unregistered shell {ShellId} for window {WindowId}, tab {TabId}", 
                    descriptor.ShellId, descriptor.AppWindowId, descriptor.TabId);
            }
        }

        public IpcShellDescriptor? GetActiveForWindow(uint appWindowId)
        {
            return _shells.Values
                .Where(d => d.AppWindowId == appWindowId && d.IsActive)
                .FirstOrDefault();
        }

        public IpcShellDescriptor? GetById(Guid shellId)
        {
            return _shells.TryGetValue(shellId, out var descriptor) ? descriptor : null;
        }

        public void SetActive(Guid shellId)
        {
            if (!_shells.TryGetValue(shellId, out var descriptor))
            {
                _logger.LogWarning("Cannot set active - shell {ShellId} not found", shellId);
                return;
            }

            // Deactivate other shells in the same window
            var windowId = descriptor.AppWindowId;
            foreach (var kvp in _shells)
            {
                if (kvp.Value.AppWindowId == windowId)
                {
                    var isActive = kvp.Key == shellId;
                    if (kvp.Value.IsActive != isActive)
                    {
                        _shells.TryUpdate(kvp.Key, kvp.Value with { IsActive = isActive }, kvp.Value);
                    }
                }
            }

            _logger.LogDebug("Set shell {ShellId} as active for window {WindowId}", shellId, windowId);
        }

        public IReadOnlyCollection<IpcShellDescriptor> List()
        {
            return _shells.Values.ToList().AsReadOnly();
        }
    }
}