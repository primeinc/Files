using System.Collections.Generic;
using System.Linq;

namespace Files.App.Communication
{
    // Simple action registry for IPC system
    public sealed class ActionRegistry
    {
        private readonly HashSet<string> _allowedActions = new(System.StringComparer.OrdinalIgnoreCase)
        {
            "navigate",
            "refresh",
            "copyPath",
            "openInNewTab",
            "openInNewWindow",
            "toggleDualPane",
            "showProperties"
        };

        public bool CanExecute(string actionId, object? context = null)
        {
            if (string.IsNullOrEmpty(actionId))
                return false;
            
            return _allowedActions.Contains(actionId);
        }

        public IEnumerable<string> GetAllowedActions() => _allowedActions.ToList();

        public void RegisterAction(string actionId)
        {
            if (!string.IsNullOrEmpty(actionId))
                _allowedActions.Add(actionId);
        }
    }
}