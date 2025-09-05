using System;
using System.Collections.Generic;
using System.Linq;

namespace Files.App.Communication
{
	// Simple action registry for IPC system
	public sealed class ActionRegistry
	{
		// readonly fields
		private readonly HashSet<string> _allowedActions = new(StringComparer.OrdinalIgnoreCase)
		{
			"navigate",
			"refresh",
			"copyPath",
			"openInNewTab",
			"openInNewWindow",
			"toggleDualPane",
			"showProperties"
		};

		// Public methods
		public bool CanExecute(string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return false;

			return _allowedActions.Contains(actionId);
		}

		public IEnumerable<string> GetAllowedActions() => _allowedActions;

		public void RegisterAction(string actionId)
		{
			if (!string.IsNullOrEmpty(actionId))
				_allowedActions.Add(actionId);
		}
	}
}