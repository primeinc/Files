namespace Files.App.Communication
{
    /// <summary>
    /// Resolves the active window for IPC routing.
    /// </summary>
    public interface IWindowResolver
    {
        /// <summary>
        /// Gets the ID of the currently active window.
        /// </summary>
        uint GetActiveWindowId();
    }
}