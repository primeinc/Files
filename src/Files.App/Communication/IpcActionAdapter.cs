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
        
        // SECURITY: Limited Action Whitelist for IPC
        // ==========================================
        // This whitelist intentionally restricts which commands can be executed via IPC for security:
        //
        // SECURITY RATIONALE:
        // 1. **Defense in Depth**: Even if IPC authentication is bypassed, damage is limited
        // 2. **Principle of Least Privilege**: Only expose necessary functionality to external processes
        // 3. **Attack Surface Reduction**: Prevents exploitation of sensitive file operations
        //
        // THREATS MITIGATED:
        // - Malicious processes executing destructive file operations (delete, move, format)
        // - Unauthorized access to sensitive system folders or files
        // - Exploitation of file management commands for privilege escalation
        // - Automation of harmful batch operations
        //
        // CRITERIA FOR ADDING NEW ACTIONS:
        // ✓ Read-only or safe operations (view, copy path, refresh)
        // ✓ Non-destructive UI actions (toggle panes, show properties)
        // ✗ File system modifications (delete, move, rename, create)
        // ✗ System-level operations (format, partition, registry access)
        // ✗ Security-sensitive actions (permissions, encryption, sharing)
        //
        // SECURITY REVIEW PROCESS FOR NEW ACTIONS:
        // ========================================
        // Before adding any new action to this whitelist, complete ALL steps:
        //
        // 1. THREAT ANALYSIS:
        //    - Could this action delete or modify user data?
        //    - Could this action access sensitive information?
        //    - Could this action be used for privilege escalation?
        //    - Could this action be chained with others maliciously?
        //
        // 2. VALIDATION REQUIREMENTS:
        //    - Does the action validate all inputs?
        //    - Are path traversal attacks prevented?
        //    - Are injection attacks (command, SQL, etc.) prevented?
        //    - Is user consent required for sensitive operations?
        //
        // 3. AUDIT LOGGING:
        //    - Is the action logged with sufficient detail?
        //    - Can malicious use be detected from logs?
        //    - Are failed attempts logged?
        //
        // 4. TESTING CHECKLIST:
        //    - Test with malformed inputs
        //    - Test with path traversal attempts (../, ..\)
        //    - Test with extremely long inputs
        //    - Test with special characters and Unicode
        //    - Test rate limiting behavior
        //
        // 5. APPROVAL PROCESS:
        //    - Document the security rationale in PR description
        //    - Get security team review for any filesystem operations
        //    - Update this comment with new action's security notes
        //
        // Each addition MUST be security-reviewed against ALL criteria above.
        // When in doubt, err on the side of caution and REJECT the action.
        private readonly Dictionary<string, CommandCodes> _actionMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["refresh"] = CommandCodes.RefreshItems,
            ["copypath"] = CommandCodes.CopyPath,
            ["toggledualpane"] = CommandCodes.ToggleDualPane,
            ["showproperties"] = CommandCodes.OpenProperties,
            // Add more mappings as needed - MUST follow security review process above
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
            
            // Local function to handle focus operations (DRY principle)
            async Task SetFocusWithEventAsync(Microsoft.UI.Xaml.UIElement element, int timeoutMs, string operation, int elementId)
            {
                var focusCompleted = new TaskCompletionSource<bool>();
                
                void OnFocusReceived(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
                {
                    focusCompleted.TrySetResult(true);
                }
                
                element.GotFocus += OnFocusReceived;
                try
                {
                    var focusResult = element.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
                    if (!focusResult)
                    {
                        _logger.LogWarning("{Operation} Focus() returned false for element {ElementId}, action may fail", 
                            operation, elementId);
                    }
                    
                    // Wait for focus event with timeout
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
                    try
                    {
                        await focusCompleted.Task.WaitAsync(cts.Token);
                        _logger.LogDebug("{Operation} focus completed for element {ElementId}", operation, elementId);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning("{Operation} focus did not complete within timeout for element {ElementId}", 
                            operation, elementId);
                        // Continue anyway - focus might still be processing
                    }
                }
                finally
                {
                    element.GotFocus -= OnFocusReceived;
                }
            }
            
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
                        // 500ms should be plenty even on slow systems
                        await SetFocusWithEventAsync(uiElement, 500, "Setting", targetShell.GetHashCode());
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
                        // Shorter timeout for restore since it's less critical (200ms)
                        await SetFocusWithEventAsync(previousUiElement, 200, "Restoring", previousActivePane.GetHashCode());
                    }
                }
            }
        }
    }
}