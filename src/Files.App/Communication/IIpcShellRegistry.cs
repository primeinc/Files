using System;
using System.Collections.Generic;

namespace Files.App.Communication
{
    /// <summary>
    /// Registry for tracking active shell instances and their IPC adapters.
    /// </summary>
    public interface IIpcShellRegistry
    {
        /// <summary>
        /// Registers a new shell instance with its adapter.
        /// </summary>
        void Register(IpcShellDescriptor descriptor);

        /// <summary>
        /// Unregisters a shell instance when it's disposed.
        /// </summary>
        void Unregister(Guid shellId);

        /// <summary>
        /// Gets the active shell for a specific window.
        /// </summary>
        IpcShellDescriptor? GetActiveForWindow(uint appWindowId);

        /// <summary>
        /// Gets a specific shell by its ID.
        /// </summary>
        IpcShellDescriptor? GetById(Guid shellId);

        /// <summary>
        /// Marks a shell as active (called on tab focus).
        /// </summary>
        void SetActive(Guid shellId);

        /// <summary>
        /// Lists all registered shells.
        /// </summary>
        IReadOnlyCollection<IpcShellDescriptor> List();
    }

    /// <summary>
    /// Describes a registered shell instance with its IPC adapter.
    /// </summary>
    public sealed record IpcShellDescriptor(
        Guid ShellId,
        uint AppWindowId,
        Guid TabId,
        ShellIpcAdapter Adapter,
        bool IsActive);
}