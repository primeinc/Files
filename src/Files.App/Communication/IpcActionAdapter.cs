using Files.App.Data.Commands;
using Files.App.Data.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Files.App.Communication
{
    /// <summary>
    /// Adapter that bridges IPC action strings to the existing UI CommandManager.
    /// This ensures IPC actions execute exactly the same code as UI actions.
    /// </summary>
    public sealed class IpcActionAdapter
    {
        private readonly ICommandManager _commandManager;
        private readonly ILogger<IpcActionAdapter> _logger;
        
        // Map of IPC action strings to CommandCodes
        // This serves as our whitelist - only these actions are allowed via IPC
        private readonly Dictionary<string, CommandCodes> _actionMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["refresh"] = CommandCodes.RefreshItems,
            ["copypath"] = CommandCodes.CopyPath,
            ["toggledualpane"] = CommandCodes.ToggleDualPane,
            ["showproperties"] = CommandCodes.OpenProperties,
            // Add more mappings as needed
        };

        public IpcActionAdapter(ICommandManager commandManager, ILogger<IpcActionAdapter> logger)
        {
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Checks if an action is allowed via IPC.
        /// </summary>
        public bool CanExecute(string actionId)
        {
            if (string.IsNullOrEmpty(actionId))
                return false;
                
            return _actionMap.ContainsKey(actionId);
        }

        /// <summary>
        /// Gets all allowed IPC action IDs.
        /// </summary>
        public IEnumerable<string> GetAllowedActions()
        {
            return _actionMap.Keys;
        }

        /// <summary>
        /// Executes an IPC action by delegating to the UI CommandManager.
        /// </summary>
        public async Task<object> ExecuteActionAsync(string actionId, IShellPage? targetShell = null)
        {
            if (!_actionMap.TryGetValue(actionId, out var commandCode))
            {
                _logger.LogWarning("IPC action '{ActionId}' not found or not allowed", actionId);
                throw new InvalidOperationException($"Action '{actionId}' is not allowed via IPC");
            }

            _logger.LogInformation("Executing IPC action '{ActionId}' via CommandCode '{CommandCode}'", 
                actionId, commandCode);

            var command = _commandManager[commandCode];
            
            if (command.Code == CommandCodes.None)
            {
                _logger.LogError("CommandCode '{CommandCode}' not found in CommandManager", commandCode);
                throw new InvalidOperationException($"Command '{commandCode}' not found");
            }

            // WORKAROUND: Actions use ContentPageContext (singleton) which always looks at the UI's active pane.
            // When IPC executes an action on a non-active shell (e.g., after dual-pane toggle creates new shells),
            // the action's IsExecutable check fails because it's checking the wrong shell's context.
            // 
            // Solution: Temporarily focus the target shell so ContentPageContext.ShellPage points to it.
            // This ensures actions check the correct shell's state (navigation history, current folder, etc.)
            //
            // FUTURE FIX: Refactor actions to accept an IShellPage parameter instead of using singleton DI,
            // or use scoped DI containers per shell instance. This would eliminate the need for focus manipulation.
            
            IShellPage? previousActivePane = null;
            bool shouldRestoreFocus = false;
            
            if (targetShell != null && targetShell.PaneHolder != null)
            {
                previousActivePane = targetShell.PaneHolder.ActivePane;
                
                // Only change focus if different from current
                if (previousActivePane != targetShell)
                {
                    _logger.LogDebug("Temporarily focusing target shell {ShellId} for action execution", 
                        targetShell.GetHashCode());
                    
                    // Focus the shell's UI element to make it the active pane
                    if (targetShell is Microsoft.UI.Xaml.UIElement uiElement)
                    {
                        shouldRestoreFocus = true;
                        
                        // Wait for focus to complete using event-driven approach
                        var focusCompleted = new TaskCompletionSource<bool>();
                        
                        void OnGotFocus(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
                        {
                            focusCompleted.TrySetResult(true);
                        }
                        
                        uiElement.GotFocus += OnGotFocus;
                        try
                        {
                            var focusResult = uiElement.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
                            if (!focusResult)
                            {
                                _logger.LogWarning("Focus() returned false for shell {ShellId}, action may fail", 
                                    targetShell.GetHashCode());
                            }
                            
                            // Wait for focus event with timeout (500ms should be plenty even on slow systems)
                            // If focus completes faster, we continue immediately
                            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                            try
                            {
                                await focusCompleted.Task.WaitAsync(cts.Token);
                                _logger.LogDebug("Focus completed for shell {ShellId}", targetShell.GetHashCode());
                            }
                            catch (OperationCanceledException)
                            {
                                _logger.LogWarning("Focus did not complete within timeout for shell {ShellId}", 
                                    targetShell.GetHashCode());
                                // Continue anyway - focus might still be processing
                            }
                        }
                        finally
                        {
                            uiElement.GotFocus -= OnGotFocus;
                        }
                    }
                }
            }

            try
            {
                if (!command.IsExecutable)
                {
                    // Gather context information to help diagnose why the command isn't executable
                    var contextInfo = new System.Text.StringBuilder();
                    
                    if (targetShell != null)
                    {
                        contextInfo.AppendLine($"Target Shell: {targetShell.GetHashCode()}");
                        contextInfo.AppendLine($"Is Current Pane: {targetShell.IsCurrentPane}");
                        contextInfo.AppendLine($"Page Type: {targetShell.CurrentPageType?.Name ?? "null"}");
                        
                        if (targetShell.ShellViewModel != null)
                        {
                            contextInfo.AppendLine($"Working Directory: {targetShell.ShellViewModel.WorkingDirectory ?? "null"}");
                            contextInfo.AppendLine($"Has Selection: {targetShell.SlimContentPage?.SelectedItems?.Count > 0}");
                        }
                    }
                    else
                    {
                        contextInfo.AppendLine("Target Shell: null");
                    }
                    
                    _logger.LogWarning("Command '{CommandCode}' is not executable. Context: {Context}", 
                        commandCode, contextInfo.ToString());
                    
                    throw new InvalidOperationException(
                        $"Command '{commandCode}' cannot be executed in the current context. " +
                        $"Page type: {targetShell?.CurrentPageType?.Name ?? "unknown"}, " +
                        $"Has selection: {targetShell?.SlimContentPage?.SelectedItems?.Count > 0}");
                }

                await command.ExecuteAsync();
                
                return new { status = "ok", command = commandCode.ToString() };
            }
            finally
            {
                // Restore original focus to prevent UI state changes from IPC operations
                if (shouldRestoreFocus && previousActivePane != null)
                {
                    _logger.LogDebug("Restoring focus to previous pane {PaneId}", 
                        previousActivePane.GetHashCode());
                    
                    if (previousActivePane is Microsoft.UI.Xaml.UIElement previousUiElement)
                    {
                        // Use same event-driven approach for restore
                        var restoreCompleted = new TaskCompletionSource<bool>();
                        
                        void OnRestoreFocus(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
                        {
                            restoreCompleted.TrySetResult(true);
                        }
                        
                        previousUiElement.GotFocus += OnRestoreFocus;
                        try
                        {
                            var restoreResult = previousUiElement.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
                            if (!restoreResult)
                            {
                                _logger.LogWarning("Focus restore failed for pane {PaneId}", 
                                    previousActivePane.GetHashCode());
                            }
                            
                            // Shorter timeout for restore since it's less critical
                            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                            try
                            {
                                await restoreCompleted.Task.WaitAsync(cts.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                // Not critical if restore times out
                                _logger.LogDebug("Focus restore timed out for pane {PaneId}", 
                                    previousActivePane.GetHashCode());
                            }
                        }
                        finally
                        {
                            previousUiElement.GotFocus -= OnRestoreFocus;
                        }
                    }
                }
            }
        }
    }
}