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

    private async Task<bool> ShowRecorderAsync(VisualRecorderWindow recorder)
    {
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler onClosed = (_, _) => closed.TrySetResult(recorder.Saved);
        _activeRecorder = recorder;
        recorder.Closed += onClosed;
        try
        {
            recorder.Show();
            var saved = await closed.Task;
            // Closed is a synchronous WPF event; recorder callback disposal
            // and Playwright drain can continue asynchronously after it fires.
            await recorder.CleanupCompleted;
            if (recorder.CleanupFailed)
                throw new InvalidOperationException("Recorder cleanup could not finish safely.");
            return saved;
        }
        catch
        {
            // A failed Show cannot leave a still-live recorder as an untracked
            // browser listener. Close is best-effort; the lock path will block
            // profile sealing if teardown ultimately fails.
            if (recorder.IsVisible)
            {
                try { recorder.Close(); } catch { /* preserve original exception */ }
            }
            throw;
        }
        finally
        {
            recorder.Closed -= onClosed;
            if (ReferenceEquals(_activeRecorder, recorder)) _activeRecorder = null;
        }
    }

    private bool CloseActiveRecorderForLock()
    {
        var recorder = _activeRecorder;
        if (recorder is null) return true;
        try
        {
            if (recorder.IsVisible) recorder.Close();
            AppendLog("Active Teach recording was stopped before browser lock/cleanup.");
            return true;
        }
        catch (Exception ex)
        {
            AppendLog("Recorder could not close; vault remains locked and profiles are retained: " + ex.GetType().Name);
            return false;
        }
    }

    // A workflow ID is a persistence key, not authority to publish an old
    // recorder into a replacement imported under that same ID.
    private bool RecorderOwnerIsCurrent(
        WebsiteWorkflow workflow, Configuration originalConfiguration,
        long originalRevision, long originalVaultSession)
    {
        return !_vaultLockInProgress && !_windowsLocked && !_exiting &&
            VaultDatabase.Current.IsUnlocked &&
            VaultDatabase.Current.SessionGeneration == originalVaultSession &&
            ReferenceEquals(_configuration, originalConfiguration) &&
            _workspaceRevision == originalRevision &&
            _configuration.Workflows.Any(w => ReferenceEquals(w, workflow)) &&
            TeachAllowed(workflow);
    }

    private async Task SaveTaughtStepsAsync(WebsiteWorkflow workflow, IPage page,
        Configuration? admittedConfiguration = null, long? admittedRevision = null,
        long? admittedVaultSession = null)
    {
        var originalConfiguration = admittedConfiguration ?? _configuration;
        var originalRevision = admittedRevision ?? _workspaceRevision;
        var originalVaultSession = admittedVaultSession ?? VaultDatabase.Current.SessionGeneration;
        if (!RecorderOwnerIsCurrent(workflow, originalConfiguration, originalRevision,
                originalVaultSession)) return;
        // No Owner: keep the main window independently scrollable while recording.
        // Position beside LaunchFlow so the pulsing RECORDING panel stays visible.
        var recorder = new VisualRecorderWindow(page);
        try
        {
            recorder.Left = Left + Math.Max(40, ActualWidth - recorder.Width - 24);
            recorder.Top = Top + 48;
        }
        catch { /* placement is best-effort before first measure */ }
        var saved = await ShowRecorderAsync(recorder);
        // Teach uses a modeless window. Return LaunchFlow to the foreground
        // after its capture window closes, without leaving the user hunting in the taskbar.
        if (!_exiting && !_windowsLocked && IsLoaded) ShowFromTray();
        if (!saved || recorder.RecordedSteps.Count == 0) return;
        if (!RecorderOwnerIsCurrent(workflow, originalConfiguration, originalRevision,
                originalVaultSession))
        {
            // Do not resolve by matching ID: imported workflows may deliberately
            // retain that ID while replacing the old object and its authority.
            AppendLog("Teach actions were discarded because the website, configuration or vault session changed.");
            return;
        }

        int previousCount = workflow.Steps.Count;
        workflow.Steps.AddRange(recorder.RecordedSteps);
        // Recorder actions are committed immediately to the encrypted vault.
        // Never report success for ConfigStore's locked-vault temp fallback.
        try { _store.SaveDurable(originalConfiguration, originalVaultSession); }
        catch
        {
            workflow.Steps.RemoveRange(previousCount, workflow.Steps.Count - previousCount);
            throw;
        }
        if (ReferenceEquals(_editorSession.Selection, workflow)) RefreshSteps();
        if (!_editorSession.IsDirty) MarkSaved();
        AppendLog($"Saved {recorder.RecordedSteps.Count} taught action(s) for '{workflow.Name}'.");
    }

    private async Task RunTeachOperationAsync(
        WebsiteWorkflow workflow, Configuration admittedConfiguration,
        long admittedRevision, long admittedVaultSession)
    {
        var page = await _runner.GetPageForRecorderAsync(workflow);
        if (!RecorderOwnerIsCurrent(workflow, admittedConfiguration, admittedRevision,
                admittedVaultSession)) return;
        await SaveTaughtStepsAsync(workflow, page, admittedConfiguration, admittedRevision,
            admittedVaultSession);
    }

    private async void RecordSteps_Click(object sender, RoutedEventArgs e)
    {
        if (_teachUi.IsBusy || _runUi.IsRunning || _vaultLockInProgress || _windowsLocked || _exiting ||
            _editorSession.Selection is null || !VaultDatabase.Current.IsUnlocked || !TeachAllowed(_editorSession.Selection) || !SaveAll()) return;
        var workflow = _editorSession.Selection;
        if (!EnsureFirefoxAutomationReady(new[] { workflow })) return;
        if (!_teachUi.TryBegin(out var teachLease)) return;
        var admittedConfiguration = _configuration;
        var admittedRevision = _workspaceRevision;
        var admittedVaultSession = VaultDatabase.Current.SessionGeneration;
        UpdateRunButtons();
        // The tracked task covers acquisition, modeless recording, callback
        // drain and publication, not merely GetPageForRecorderAsync.
        var operation = RunTeachOperationAsync(workflow, admittedConfiguration,
            admittedRevision, admittedVaultSession);
        if (!_teachUi.TrackTask(teachLease, operation))
        {
            _teachUi.Complete(teachLease);
            UpdateRunButtons();
            return;
        }
        try { await operation; }
        catch (Exception ex)
        {
            if (!_vaultLockInProgress && !_windowsLocked && !_exiting)
                MessageBox.Show(this, MakeBrowserFriendlyMessage(ex),
                    "Record clicks", MessageBoxButton.OK, MessageBoxImage.Information);
            else AppendLog("Teach was stopped during vault/session cleanup: " + ex.GetType().Name);
        }
        finally
        {
            _teachUi.Complete(teachLease);
            UpdateRunButtons();
        }
    }

    private async void RepairLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_teachUi.IsBusy || _runUi.IsRunning || _vaultLockInProgress || _windowsLocked || _exiting) return;
        try { await RepairLoginCoreAsync(); }
        catch (Exception ex)
        {
            if (!_vaultLockInProgress && !_windowsLocked && !_exiting)
                MessageBox.Show(this, MakeBrowserFriendlyMessage(ex),
                    "Fix sign-in", MessageBoxButton.OK, MessageBoxImage.Information);
            else AppendLog("Repair was stopped during vault/session cleanup: " + ex.GetType().Name);
        }
    }

    private async Task RunRepairOperationAsync(
        WebsiteWorkflow workflow, Configuration admittedConfiguration,
        long admittedRevision, long admittedVaultSession)
    {
        // Repair is a modeless Teach operation with a health inspection.
        AppendLog($"Repair login for '{workflow.Name}': sign in in the browser, teach safe clicks, then Save actions.");
        WorkflowStatusLabel.Text = "Repair login · Use the browser and modeless Teach window";
        if (!RecorderOwnerIsCurrent(workflow, admittedConfiguration, admittedRevision,
                admittedVaultSession)) return;
        var page = await _runner.GetPageForRecorderAsync(workflow);
        if (!RecorderOwnerIsCurrent(workflow, admittedConfiguration, admittedRevision,
                admittedVaultSession)) return;
        var health = await LoginHealth.InspectAsync(page);
        if (health.NeedsAttention) AppendLog($"Repair login: {health.Reason}");
        if (!RecorderOwnerIsCurrent(workflow, admittedConfiguration, admittedRevision,
                admittedVaultSession)) return;
        await SaveTaughtStepsAsync(workflow, page, admittedConfiguration, admittedRevision,
            admittedVaultSession);
    }

    private async Task RepairLoginCoreAsync(WebsiteWorkflow? target = null)
    {
        var workflow = target ?? _editorSession.Selection;
        if (_teachUi.IsBusy || _runUi.IsRunning || _runner.IsRunning ||
            _vaultLockInProgress || _windowsLocked || _exiting ||
            workflow is null || !VaultDatabase.Current.IsUnlocked || !TeachAllowed(workflow)) return;
        if (!EnsureFirefoxAutomationReady(new[] { workflow })) return;
        if (!_teachUi.TryBegin(out var teachLease)) return;

        var admittedConfiguration = _configuration;
        var admittedRevision = _workspaceRevision;
        var admittedVaultSession = VaultDatabase.Current.SessionGeneration;
        UpdateRunButtons();
        try
        {
            if (!SaveAll() || _runUi.IsRunning || _runner.IsRunning ||
                !RecorderOwnerIsCurrent(workflow, admittedConfiguration, admittedRevision,
                    admittedVaultSession)) return;

            var operation = RunRepairOperationAsync(workflow, admittedConfiguration,
                admittedRevision, admittedVaultSession);
            if (!_teachUi.TrackTask(teachLease, operation)) return;
            await operation;
        }
        finally
        {
            _teachUi.Complete(teachLease);
            UpdateRunButtons();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _runUi.RequestStop();
        UpdateRunButtons();
    }

    private async Task RunEnabledAsync(bool onlySelected = false, WorkspaceProfile? workspace = null)
    {
        if (_runUi.IsRunning || _runner.IsRunning || _vaultLockInProgress || _windowsLocked || _teachUi.IsBusy || _exiting || _finalRunnerDisposeTask is not null || !AutomationAllowed()) return;
        // Own the UI operation before any modal prompt can pump another hotkey/tray event.
        var launchRevision = _workspaceRevision;
        if (!_runUi.TryBeginRun(out var runLease)) return;
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
                PublishKeePassCache(null);
                AppendLog("Configuration was replaced while authorizing the run. Run again using the current settings.");
                return;
            }
            if (!SaveAll()) return;
            var toRun = onlySelected
                ? (_editorSession.Selection is null ? new List<WebsiteWorkflow>() : new List<WebsiteWorkflow> { _editorSession.Selection })
                : workspace is not null
                    ? workspace.Resolve(_configuration).ToList()
                    : _configuration.Workflows.Where(workflow => workflow.Enabled).ToList();
            if (toRun.Count == 0) { AppendLog("No enabled workflows to run."); return; }
            if (!EnsureFirefoxAutomationReady(toRun)) return;
            if (!WorkspaceCatalog.PendingRunIsCurrent(
                    _configuration, toRun, onlySelected, workspace, launchRevision, _workspaceRevision) ||
                (onlySelected && !ReferenceEquals(_editorSession.Selection, toRun[0])) ||
                (workspace is not null && (!ReferenceEquals(WorkspaceCombo.SelectedItem, workspace) ||
                    !WorkspaceCatalog.IsCurrentSelection(_configuration, workspace, WorkspaceNameBox.Text))))
            {
                PublishKeePassCache(null);
                AppendLog("Workflow selection changed before launch. Run again using the current configuration.");
                return;
            }
            if (toRun.Any(workflow => !AutomationAllowed(workflow))) return;
            if (!EnsureKeePassPasswordForRun(toRun)) return;
            if (_windowsLocked || _vaultLockInProgress || _exiting || !VaultDatabase.Current.IsUnlocked ||
                !WorkspaceCatalog.PendingRunIsCurrent(
                    _configuration, toRun, onlySelected, workspace, launchRevision, _workspaceRevision) ||
                (onlySelected && !ReferenceEquals(_editorSession.Selection, toRun[0])) ||
                (workspace is not null && (!ReferenceEquals(WorkspaceCombo.SelectedItem, workspace) ||
                    !WorkspaceCatalog.IsCurrentSelection(_configuration, workspace, WorkspaceNameBox.Text))) ||
                toRun.Any(workflow => !AutomationAllowed(workflow)))
            {
                PublishKeePassCache(null);
                return;
            }
            var monitors = MonitorService.GetMonitors();
            foreach (var workflow in toRun)
                workflow.MonitorIndex = MonitorService.Resolve(workflow, monitors).Index;
            cancellation = new CancellationTokenSource();
            if (!_runUi.AttachCancellation(runLease, cancellation)) return;
            UpdateRunButtons();
            WorkflowStatusLabel.Text = LocalizationService.Text("Running", _configuration.UiLanguage);
            _runUi.BeginStatus(runLease);
            var engineTask = _runner.RunAsync(toRun, cancellation.Token,
                includeDisabled: onlySelected, configuration: _configuration, keepassMasterPassword: _keepassMasterPassword);
            if (!_runUi.TrackTask(runLease, engineTask))
                throw new InvalidOperationException("UI lease changed during engine task registration.");
            var result = await engineTask;
            _runUi.EndStatus(runLease);
            if (cancellation.IsCancellationRequested || _windowsLocked || _vaultLockInProgress || _exiting) return;
            PersistLearnedLoginRecipes(result);
            var attention = result.LoginNeedsAttention;
            var steps = result.StepsNeedAttention;
            if (RunOutcomeSummary.HasProblems(result.FailedCount, attention.Count, steps.Count))
            {
                var singleFailure = result.FailedCount == 1 && result.Failures.Count == 1
                    ? result.Failures[0].Reason : null;
                WorkflowStatusLabel.Text = RunOutcomeSummary.StatusLine(
                    "Couldn't finish", result.FailedCount,
                    "Needs a moment", attention.Count,
                    steps.Count);
                // Calm tray note — never Warning/Error balloons for ordinary run misses.
                NotifyTray("LaunchFlow",
                    RunOutcomeSummary.TrayDetail(result.FailedCount, attention.Count, steps.Count,
                        attention.Count == 1 ? attention[0].Reason : null, singleFailure),
                    Forms.ToolTipIcon.None);
                if (!string.IsNullOrWhiteSpace(singleFailure))
                {
                    AppendLog(singleFailure);
                    if (RunOutcomeSummary.IsFirefoxAutomationFailure(singleFailure))
                        OfferFirefoxAutomationHelp(singleFailure);
                }
                if (attention.Count > 0) await OfferRepairForLoginAttentionAsync(attention);
                if (steps.Count > 0) OfferRepairForStepAttention(steps);
            }
            else
            {
                WorkflowStatusLabel.Text = LocalizationService.Text("Finished", _configuration.UiLanguage);
                NotifyTray("LaunchFlow",
                    RunOutcomeSummary.FinishedTray(toRun.Count, toRun.Count == 1 ? toRun[0].Name : null),
                    Forms.ToolTipIcon.None);
            }
        }
        catch (OperationCanceledException)
        {
            WorkflowStatusLabel.Text = LocalizationService.Text("Stopped", _configuration.UiLanguage);
            if (!_exiting) NotifyTray("LaunchFlow", "Stopped — nothing else changed.", Forms.ToolTipIcon.None);
        }
        catch (Exception ex)
        {
            WorkflowStatusLabel.Text = "Couldn't finish this time";
            AppendLog("Run error: " + ex.GetType().Name);
            if (!_exiting) NotifyTray("LaunchFlow", "Couldn't finish this time. Your sites and passwords are fine — try again when ready.", Forms.ToolTipIcon.None);
        }
        finally
        {
            _runUi.EndStatus(runLease);
            if (cancellation?.IsCancellationRequested == true)
                WorkflowStatusLabel.Text = LocalizationService.Text("Stopped", _configuration.UiLanguage);
            _runUi.Complete(runLease);
            cancellation?.Dispose();
            UpdateRunButtons();
        }
    }

    private Task OfferRepairForLoginAttentionAsync(
        IReadOnlyList<AutomationEngine.LoginAttention> items)
    {
        // Quiet help only — never yank the app out of the tray or stack scary
        // modals. Status + an optional small modeless note is enough.
        if (MainTabs.SelectedIndex != 0 && IsVisible && WindowState != WindowState.Minimized)
            MainTabs.SelectedIndex = 0;
        foreach (var item in items)
        {
            if (_vaultLockInProgress || _windowsLocked || _exiting ||
                !VaultDatabase.Current.IsUnlocked || _runUi.Cancellation?.IsCancellationRequested == true)
                break;
            var workflow = _configuration.Workflows.FirstOrDefault(w => w.Id == item.WorkflowId);
            if (workflow is null) continue;
            // A visible password challenge is evidence sign-in is incomplete,
            // not a question about whether the user reached the dashboard.
            if (item.Reason.StartsWith("Password stage still visible", StringComparison.Ordinal))
            {
                WorkflowList.SelectedItem = workflow;
                WorkflowStatusLabel.Text = "Password step is open in the browser · finish there when ready";
                AppendLog($"Sign-in for '{workflow.Name}' paused at the password step.");
                continue;
            }
            if (_openSignInDecisions.TryGetValue(workflow.Id, out var existing))
            {
                // Already offered once this session — don't shove another window forward.
                continue;
            }
            WorkflowList.SelectedItem = workflow;
            WorkflowStatusLabel.Text = "Almost there · check the browser for " + workflow.Name;
            AppendLog($"Sign-in for '{workflow.Name}': {item.Reason}");
            // Only show the quiet helper if LaunchFlow is already on screen.
            if (!IsVisible || WindowState == WindowState.Minimized) continue;
            var capturedRevision = _workspaceRevision;
            var dialog = new SignInDecisionWindow(item.WorkflowName, item.Reason) { Owner = this };
            _openSignInDecisions.Add(workflow.Id, dialog);
            dialog.Closed += (_, _) =>
            {
                if (_openSignInDecisions.TryGetValue(workflow.Id, out var current) &&
                    ReferenceEquals(current, dialog))
                    _openSignInDecisions.Remove(workflow.Id);
                _ = ApplyModelessSignInDecisionAsync(workflow, capturedRevision, dialog.Choice);
            };
            try { dialog.Show(); }
            catch
            {
                _openSignInDecisions.Remove(workflow.Id);
                AppendLog("Sign-in tip window could not open — status bar still has the note.");
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

        if (_runUi.IsRunning || _runner.IsRunning || _teachUi.IsBusy ||
            !TeachAllowed(workflow)) return;
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
                _runUi.Cancellation?.IsCancellationRequested == true) return;
            var workflow = _configuration.Workflows.FirstOrDefault(w => w.Id == item.WorkflowId);
            if (workflow is null) continue;
            if (IsVisible && WindowState != WindowState.Minimized)
            {
                MainTabs.SelectedIndex = 0;
                WorkflowList.SelectedItem = workflow;
            }
            AppendLog($"A recorded {item.StepType} for '{item.WorkflowName}' needs a quick look. Later steps were skipped.");
            NotifyTray("LaunchFlow",
                $"A recorded step for “{item.WorkflowName}” needs a quick look when you’re ready.",
                Forms.ToolTipIcon.None);
        }
    }

    private void PersistLearnedLoginRecipes(AutomationEngine.RunResult result)
    {
        if (result.LoginRecipesUpdated.Count == 0 || _editorSession.IsDirty ||
            !VaultDatabase.Current.IsUnlocked || _windowsLocked || _vaultLockInProgress || _exiting)
            return;
        try
        {
            var session = VaultDatabase.Current.SessionGeneration;
            _store.SaveDurable(_configuration, session);
            _store.NoteCommittedExternally();
            if (!_editorSession.IsDirty) MarkSaved();
            AppendLog($"Saved remembered login shape(s) for {result.LoginRecipesUpdated.Count} website(s).");
            RefreshLoginRecipeLabel();
        }
        catch (Exception ex)
        {
            // Preserve this in-memory recipe change as dirty if the immediate
            // durable write fails. Clean-exit fast path must not drop it.
            MarkUnsavedChanges();
            AppendLog("Login shape was learned in memory but not saved yet: " + ex.GetType().Name);
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

    /// <summary>
    /// Regular Mozilla Firefox cannot be automated. Stop before a blank window
    /// and the generic "1 workflow(s) failed" toast.
    /// </summary>
    private bool EnsureFirefoxAutomationReady(IReadOnlyList<WebsiteWorkflow> toRun)
    {
        var needsFirefox = toRun.Any(w =>
            w.Browser.Contains("Firefox", StringComparison.OrdinalIgnoreCase));
        if (!needsFirefox || PlaywrightFirefoxSupport.IsAutomationFirefoxInstalled())
            return true;

        ShowFromTray();
        var choice = MessageBox.Show(this,
            "This website is set to Firefox, and LaunchFlow’s Firefox isn’t installed yet.\n\n" +
            "Yes = open Browsers on this PC to install it.\n" +
            "No = cancel for now — you can switch this website to Edge, Chrome, Brave, Opera, or Vivaldi anytime.",
            "Firefox setup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (choice == MessageBoxResult.Yes)
            new BrowserSetupWindow { Owner = this }.ShowDialog();
        // Only mark Failed when automation Firefox is still missing. A successful
        // consent install must let Teach/Repair/Run continue without a false cancel.
        if (PlaywrightFirefoxSupport.IsAutomationFirefoxInstalled())
            return true;
        AppendLog("Run cancelled: Firefox is not ready. Use Check browsers on this PC, or pick Edge/Chrome/Brave/Opera/Vivaldi.");
        WorkflowStatusLabel.Text = LocalizationService.Text("Failed", _configuration.UiLanguage);
        return false;
    }

    private void OfferFirefoxAutomationHelp(string reason)
    {
        ShowFromTray();
        var choice = MessageBox.Show(this,
            reason + "\n\nWant to open Browsers on this PC to install Firefox, or switch this website to Edge, Chrome, Brave, Opera, or Vivaldi?",
            "Firefox setup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (choice == MessageBoxResult.Yes)
            new BrowserSetupWindow { Owner = this }.ShowDialog();
    }

}
