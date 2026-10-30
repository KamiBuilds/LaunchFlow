using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Win32;
using Microsoft.Playwright;
using Forms = System.Windows.Forms;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace LaunchFlow;

/// <summary>Workflow runs, recorder sessions and user-attention orchestration.</summary>
public partial class MainWindow
{
    private void Run_Click(object sender, RoutedEventArgs e) => _ = RunEnabledAsync();
    private void RunSelected_Click(object sender, RoutedEventArgs e) => _ = RunEnabledAsync(onlySelected: true);

    private static Task<bool> ShowRecorderAsync(VisualRecorderWindow recorder)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.Closed += (_, _) => completion.TrySetResult(recorder.Saved);
        try { recorder.Show(); }
        catch (Exception ex) { completion.TrySetException(ex); }
        return completion.Task;
    }

    private async Task SaveTaughtStepsAsync(WebsiteWorkflow workflow, IPage page)
    {
        if (_vaultLockInProgress || _windowsLocked || _exiting || !VaultDatabase.Current.IsUnlocked || !TeachAllowed(workflow)) return;
        var recorder = new VisualRecorderWindow(page) { Owner = this };
        var saved = await ShowRecorderAsync(recorder);
        // Teach uses a modeless window. Return LaunchFlow to the foreground
        // after its capture window closes, without leaving the user hunting in the taskbar.
        if (!_exiting && !_windowsLocked && IsLoaded) ShowFromTray();
        if (!saved || recorder.RecordedSteps.Count == 0) return;
        if (_vaultLockInProgress || _windowsLocked || _exiting || !VaultDatabase.Current.IsUnlocked || !TeachAllowed(workflow)) return;

        // Teach remains modeless; the selected website may have changed.
        var original = _configuration.Workflows.FirstOrDefault(w => w.Id == workflow.Id);
        if (original is null)
        {
            MessageBox.Show(this,
                "This website was removed while Teach was open, so no clicks were saved.",
                "Website no longer exists", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int previousCount = original.Steps.Count;
        original.Steps.AddRange(recorder.RecordedSteps);
        try { _store.Save(_configuration); }
        catch
        {
            original.Steps.RemoveRange(previousCount, original.Steps.Count - previousCount);
            throw;
        }
        if (_selected?.Id == original.Id) RefreshSteps();
        if (!_hasUnsavedChanges) MarkSaved();
        AppendLog($"Saved {recorder.RecordedSteps.Count} taught action(s) for '{original.Name}'.");
    }

    private async void RecordSteps_Click(object sender, RoutedEventArgs e)
    {
        if (_teachingInProgress || _runInProgress || _vaultLockInProgress || _windowsLocked || _exiting || _selected is null || !VaultDatabase.Current.IsUnlocked || !TeachAllowed(_selected) || !SaveAll()) return;
        var workflow = _selected;
        _teachingInProgress = true;
        UpdateRunButtons();
        try
        {
            _activeTeachTask = _runner.GetPageForRecorderAsync(workflow);
            var page = await _activeTeachTask;
            if (_vaultLockInProgress || _windowsLocked || _exiting || !VaultDatabase.Current.IsUnlocked || !TeachAllowed(workflow)) return;
            await SaveTaughtStepsAsync(workflow, page);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, MakeBrowserFriendlyMessage(ex),
                "Teach clicks", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _activeTeachTask = null;
            _teachingInProgress = false;
            UpdateRunButtons();
        }
    }

    private async void RepairLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_teachingInProgress || _runInProgress || _vaultLockInProgress || _windowsLocked || _exiting) return;
        try { await RepairLoginCoreAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, MakeBrowserFriendlyMessage(ex),
                "Repair login", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RepairLoginCoreAsync(WebsiteWorkflow? target = null)
    {
        var workflow = target ?? _selected;
        if (_teachingInProgress || _vaultLockInProgress || _windowsLocked || _exiting || workflow is null ||
            !VaultDatabase.Current.IsUnlocked || !TeachAllowed(workflow) || !SaveAll()) return;
        _teachingInProgress = true;
        UpdateRunButtons();
        try
        {
            // Instructions already live in the modeless Teach recorder.
            // A blocking notice here prevented users from clicking their browser
            // to sign in, which is precisely what Repair asks them to do.
            AppendLog($"Repair login for '{workflow.Name}': sign in in the browser, teach safe clicks, then Save actions.");
            WorkflowStatusLabel.Text = "Repair login · Use the browser and modeless Teach window";
            if (_vaultLockInProgress || _windowsLocked || _exiting || !VaultDatabase.Current.IsUnlocked || !TeachAllowed(workflow)) return;
            _activeTeachTask = _runner.GetPageForRecorderAsync(workflow);
            var page = await _activeTeachTask;
            if (_vaultLockInProgress || _windowsLocked || _exiting || !VaultDatabase.Current.IsUnlocked || !TeachAllowed(workflow)) return;
            var health = await LoginHealth.InspectAsync(page);
            if (health.NeedsAttention) AppendLog($"Repair login: {health.Reason}");
            await SaveTaughtStepsAsync(workflow, page);
        }
        finally
        {
            _activeTeachTask = null;
            _teachingInProgress = false;
            UpdateRunButtons();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _runCancellation?.Cancel();
        UpdateRunButtons();
    }

    private async Task RunEnabledAsync(bool onlySelected = false, WorkspaceProfile? workspace = null)
    {
        if (_runInProgress || _runner.IsRunning || _vaultLockInProgress || _windowsLocked || _teachingInProgress || _exiting || !AutomationAllowed()) return;
        // Own the UI operation before any modal prompt can pump another hotkey/tray event.
        var launchRevision = _workspaceRevision;
        _runInProgress = true;
        UpdateRunButtons();
        CancellationTokenSource? cancellation = null;
        try
        {
            if (!VaultDatabase.Current.IsUnlocked)
            {
                ShowFromTray();
                if (!RequireMasterPassword(forcePrompt: true)) return;
            }
            if (_windowsLocked || _vaultLockInProgress || _exiting) return;
            if (launchRevision != _workspaceRevision)
            {
                _keepassMasterPassword = null;
                AppendLog("Configuration was replaced while authorizing the run. Run again using the current settings.");
                return;
            }
            if (!SaveAll()) return;
            var toRun = onlySelected
                ? (_selected is null ? new List<WebsiteWorkflow>() : new List<WebsiteWorkflow> { _selected })
                : workspace is not null
                    ? workspace.Resolve(_configuration).ToList()
                    : _configuration.Workflows.Where(workflow => workflow.Enabled).ToList();
            if (toRun.Count == 0) { AppendLog("No enabled workflows to run."); return; }
            if (!WorkspaceCatalog.PendingRunIsCurrent(
                    _configuration, toRun, onlySelected, workspace, launchRevision, _workspaceRevision) ||
                (onlySelected && !ReferenceEquals(_selected, toRun[0])) ||
                (workspace is not null && (!ReferenceEquals(WorkspaceCombo.SelectedItem, workspace) ||
                    !WorkspaceCatalog.IsCurrentSelection(_configuration, workspace, WorkspaceNameBox.Text))))
            {
                _keepassMasterPassword = null;
                AppendLog("Workflow selection changed before launch. Run again using the current configuration.");
                return;
            }
            if (toRun.Any(workflow => !AutomationAllowed(workflow))) return;
            if (!EnsureKeePassPasswordForRun(toRun)) return;
            if (_windowsLocked || _vaultLockInProgress || _exiting || !VaultDatabase.Current.IsUnlocked ||
                !WorkspaceCatalog.PendingRunIsCurrent(
                    _configuration, toRun, onlySelected, workspace, launchRevision, _workspaceRevision) ||
                (onlySelected && !ReferenceEquals(_selected, toRun[0])) ||
                (workspace is not null && (!ReferenceEquals(WorkspaceCombo.SelectedItem, workspace) ||
                    !WorkspaceCatalog.IsCurrentSelection(_configuration, workspace, WorkspaceNameBox.Text))) ||
                toRun.Any(workflow => !AutomationAllowed(workflow)))
            {
                _keepassMasterPassword = null;
                return;
            }
            var monitors = MonitorService.GetMonitors();
            foreach (var workflow in toRun)
                workflow.MonitorIndex = MonitorService.Resolve(workflow, monitors).Index;
            cancellation = new CancellationTokenSource();
            _runCancellation = cancellation;
            UpdateRunButtons();
            WorkflowStatusLabel.Text = LocalizationService.Text("Running", _configuration.UiLanguage);
            _runStatusActive = true;
            _activeRunTask = _runner.RunAsync(toRun, cancellation.Token,
                includeDisabled: onlySelected, configuration: _configuration, keepassMasterPassword: _keepassMasterPassword);
            var result = await _activeRunTask;
            _runStatusActive = false;
            if (cancellation.IsCancellationRequested || _windowsLocked || _vaultLockInProgress || _exiting) return;
            var attention = result.LoginNeedsAttention;
            var steps = result.StepsNeedAttention;
            if (result.FailedCount > 0 || steps.Count > 0 || attention.Count > 0)
            {
                WorkflowStatusLabel.Text = $"{LocalizationService.Text("Failed", _configuration.UiLanguage)}: {result.FailedCount} · " +
                    $"{LocalizationService.Text("Sign-in needs attention", _configuration.UiLanguage)}: {attention.Count} · Steps needing repair: {steps.Count}";
                NotifyTray("LaunchFlow", $"{result.FailedCount} workflow(s) failed; {attention.Count} sign-in(s) need attention; {steps.Count} step repair(s). Nothing is rerun automatically.", Forms.ToolTipIcon.Warning);
                if (attention.Count > 0) await OfferRepairForLoginAttentionAsync(attention);
                if (steps.Count > 0) OfferRepairForStepAttention(steps);
            }
            else
            {
                WorkflowStatusLabel.Text = LocalizationService.Text("Finished", _configuration.UiLanguage);
                NotifyTray("LaunchFlow", toRun.Count == 1 ? $"Finished “{toRun[0].Name}”." : $"Finished {toRun.Count} workflows.");
            }
        }
        catch (OperationCanceledException)
        {
            WorkflowStatusLabel.Text = LocalizationService.Text("Stopped", _configuration.UiLanguage);
            if (!_exiting) NotifyTray("LaunchFlow", "Workflow run was stopped.");
        }
        catch (Exception ex)
        {
            WorkflowStatusLabel.Text = LocalizationService.Text("Failed", _configuration.UiLanguage);
            AppendLog("Run error: " + ex.GetType().Name);
            if (!_exiting) NotifyTray("LaunchFlow", "A workflow failed. Review the website settings and try again.", Forms.ToolTipIcon.Error);
        }
        finally
        {
            _runStatusActive = false;
            if (cancellation?.IsCancellationRequested == true)
                WorkflowStatusLabel.Text = LocalizationService.Text("Stopped", _configuration.UiLanguage);
            if (ReferenceEquals(_runCancellation, cancellation)) _runCancellation = null;
            cancellation?.Dispose();
            _activeRunTask = null;
            _runInProgress = false;
            UpdateRunButtons();
        }
    }

    private Task OfferRepairForLoginAttentionAsync(
        IReadOnlyList<AutomationEngine.LoginAttention> items)
    {
        // Informational sign-in assistance is modeless. A completed run must
        // release its UI state immediately, even if the user leaves a prompt
        // open while reviewing other websites.
        ShowFromTray();
        MainTabs.SelectedIndex = 0;
        foreach (var item in items)
        {
            if (_vaultLockInProgress || _windowsLocked || _exiting ||
                !VaultDatabase.Current.IsUnlocked || _runCancellation?.IsCancellationRequested == true)
                break;
            var workflow = _configuration.Workflows.FirstOrDefault(w => w.Id == item.WorkflowId);
            if (workflow is null) continue;
            if (_openSignInDecisions.TryGetValue(workflow.Id, out var existing))
            {
                // Do not flood the user with identical windows on repeated runs.
                existing.Activate();
                continue;
            }
            WorkflowList.SelectedItem = workflow;
            var capturedRevision = _workspaceRevision;
            var dialog = new SignInDecisionWindow(item.WorkflowName, item.Reason) { Owner = this };
            _openSignInDecisions.Add(workflow.Id, dialog);
            dialog.Closed += (_, _) =>
            {
                if (_openSignInDecisions.TryGetValue(workflow.Id, out var current) &&
                    ReferenceEquals(current, dialog))
                    _openSignInDecisions.Remove(workflow.Id);
                // Closed fires synchronously; async work continues without
                // disabling the main window or showing any modal repair dialog.
                _ = ApplyModelessSignInDecisionAsync(workflow, capturedRevision, dialog.Choice);
            };
            try { dialog.Show(); }
            catch
            {
                _openSignInDecisions.Remove(workflow.Id);
                // A failed optional notice cannot turn a finished run into
                // an unsafe retry or leak private Playwright error details.
                AppendLog("Could not display the sign-in assistance window.");
            }
        }
        return Task.CompletedTask;
    }

    private async Task ApplyModelessSignInDecisionAsync(
        WebsiteWorkflow workflow, long capturedRevision, SignInDecisionWindow.Decision choice)
    {
        if (choice == SignInDecisionWindow.Decision.Later ||
            _exiting || _windowsLocked || _vaultLockInProgress ||
            !VaultDatabase.Current.IsUnlocked ||
            capturedRevision != _workspaceRevision ||
            !_configuration.Workflows.Any(w => ReferenceEquals(w, workflow))) return;

        if (choice == SignInDecisionWindow.Decision.Confirmed)
        {
            try
            {
                if (await _runner.ConfirmSignedInAsync(workflow))
                {
                    if (_exiting || _windowsLocked || _vaultLockInProgress ||
                        !VaultDatabase.Current.IsUnlocked ||
                        capturedRevision != _workspaceRevision ||
                        !_configuration.Workflows.Any(w => ReferenceEquals(w, workflow))) return;
                    WorkflowStatusLabel.Text = "Signed-in session confirmed · Run this website again to continue actions";
                    AppendLog($"User confirmed the open signed-in session for '{workflow.Name}'. Run again to continue.");
                }
                else
                {
                    AppendLog($"Sign-in confirmation for '{workflow.Name}' was unavailable; check its browser challenge.");
                    WorkflowStatusLabel.Text = "Sign-in was not confirmed · Check the browser page";
                }
            }
            catch (Exception)
            {
                AppendLog("Sign-in confirmation could not be completed safely.");
            }
            return;
        }

        if (!TeachAllowed(workflow)) return;
        try { await RepairLoginCoreAsync(workflow); }
        catch (Exception ex)
        {
            if (!_exiting)
                AppendLog("Repair login could not start: " + ex.GetType().Name);
        }
    }

    private void OfferRepairForStepAttention(IReadOnlyList<AutomationEngine.StepAttention> items)
    {
        foreach (var item in items)
        {
            if (_vaultLockInProgress || _windowsLocked || _exiting || !VaultDatabase.Current.IsUnlocked ||
                _runCancellation?.IsCancellationRequested == true) return;
            var workflow = _configuration.Workflows.FirstOrDefault(w => w.Id == item.WorkflowId);
            if (workflow is null) continue;
            ShowFromTray();
            MainTabs.SelectedIndex = 0;
            WorkflowList.SelectedItem = workflow;
            // Step failures are informational; never disable the workspace
            // behind a modal alert after the automation has already stopped.
            AppendLog($"Action {item.StepType} needs review for '{item.WorkflowName}'. Later steps were not run.");
            // Preserve the final run's failed/sign-in/step counts in the status bar.
            NotifyTray("Workflow steps need review",
                $"Review the {item.StepType} action for '{item.WorkflowName}' using Edit or Teach.",
                Forms.ToolTipIcon.Warning);
        }
    }

    private void NotifyTray(string title, string message, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info)
    {
        if (!_configuration.TrayNotificationsEnabled) return;
        try
        {
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = message.Length > 240 ? message[..240] + "…" : message;
            _tray.BalloonTipIcon = icon;
            _tray.ShowBalloonTip(4500);
        }
        catch { /* tray may be unavailable */ }
    }

}
