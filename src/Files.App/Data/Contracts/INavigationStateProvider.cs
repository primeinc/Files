// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Files.App.Data.Contracts
{
	/// <summary>
	/// Abstraction for reading and controlling navigation state of the shell.
	/// </summary>
	public interface INavigationStateProvider
	{
		/// <summary>Gets the current path shown in the shell.</summary>
		string? CurrentPath { get; }

		/// <summary>True if navigating back is possible.</summary>
		bool CanGoBack { get; }

		/// <summary>True if navigating forward is possible.</summary>
		bool CanGoForward { get; }

		/// <summary>
		/// Raised when CurrentPath, CanGoBack or CanGoForward changes.
		/// </summary>
		event EventHandler? StateChanged;

		/// <summary>
		/// Navigates the shell to the given absolute path.
		/// </summary>
		Task NavigateToAsync(string path, CancellationToken ct = default);
	}
}
