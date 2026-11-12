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

/// <summary>Website creation, selection, duplication and removal.</summary>
public partial class MainWindow
{
    private void WorkflowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Item selection can be raised during XAML construction; the editor
        // and vault-backed configuration are not yet available at that point.
        if (_loadingEditor || _configuration is null || WorkflowList is null ||
            WorkflowEmptyState is null || NameBox is null) return;
        try { ApplyCurrentEditor(); }
        catch (Exception ex)
        {
            AppendLog("Could not save previous credential: " + RunIssueNotice.SafeExceptionReason(ex));
            // Keep the previous selection if applying its website fields fails.
            // Credentials have a separate explicit save path; this is not an encryption commit.
            _loadingEditor = true;
            WorkflowList.SelectedItem = _editorSession.Selection;
            _loadingEditor = false;
            return;
        }
        _editorSession.Select(WorkflowList.SelectedItem as WebsiteWorkflow);
        if (_editorSession.Selection is null) ShowEmptyEditor();
        else ShowEditor(_editorSession.Selection);
    }

    private void RefreshWorkflowList(WebsiteWorkflow? selection)
    {
        _loadingEditor = true;
        WorkflowList.Items.Refresh();
        WorkflowList.SelectedItem = selection;
        _editorSession.Select(selection);
        _loadingEditor = false;
        if (selection is null) ShowEmptyEditor();
        else ShowEditor(selection);
    }

    private static Task<bool> ShowNewWorkflowAsync(NewWorkflowWindow wizard)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        wizard.Closed += (_, _) => completion.TrySetResult(wizard.WasCreated);
        try { wizard.Show(); }
        catch (Exception ex) { completion.TrySetException(ex); }
        return completion.Task;
    }

    private async void AddWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (_vaultLockInProgress || _exiting || !RequireMasterPassword()) return;
        if (_newWorkflowWindow is not null)
        {
            _newWorkflowWindow.Activate();
            return;
        }

        try { ApplyCurrentEditor(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, RunIssueNotice.SafeExceptionReason(ex), "Cannot save previous website", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // An unowned setup window stays open when LaunchFlow is minimized.
        var wizard = new NewWorkflowWindow();
        _newWorkflowWindow = wizard;
        try
        {
            if (!await ShowNewWorkflowAsync(wizard)) return;
            if (_vaultLockInProgress || _windowsLocked || _exiting || !VaultDatabase.Current.IsUnlocked) return;
            var next = wizard.CreatedWorkflow;
            if (next is null) return;
            // Show the next-step dialog once, only for a genuinely first
            // website. Upgraded users with existing websites are not interrupted.
            var ownedConfig = _configuration;
            var ownedVaultSession = VaultDatabase.Current.SessionGeneration;
            var firstWebsite = ownedConfig.Workflows.Count == 0 &&
                !ownedConfig.FirstWebsiteGuidanceShown;
            try
            {
                // Clone → write once. Credential + website settings share one
                // vault transaction when a password is supplied; otherwise a
                // single durable config write. Never leave a secret without a site.
                var candidate = System.Text.Json.JsonSerializer.Deserialize<Configuration>(
                    System.Text.Json.JsonSerializer.Serialize(ownedConfig))
                    ?? throw new InvalidOperationException("Unable to prepare the new website snapshot.");
                candidate.Workflows.Add(next);
                if (firstWebsite)
                {
                    candidate.FirstWebsiteGuidanceShown = true;
                    candidate.ShortcutTipShown = true; // avoid a second first-use dialog on Save
                }

                if (!ReferenceEquals(_configuration, ownedConfig) ||
                    !VaultDatabase.Current.IsUnlocked ||
                    ownedVaultSession != VaultDatabase.Current.SessionGeneration ||
                    _windowsLocked || _vaultLockInProgress || _exiting)
                    throw new InvalidOperationException("The vault or configuration changed before create finished.");

                if (!string.IsNullOrEmpty(wizard.NewPassword))
                {
                    _vault.SaveWithConfiguration(next.Id,
                        new SiteCredential(wizard.NewUsername, wizard.NewPassword),
                        candidate, ownedVaultSession);
                }
                else
                {
                    _store.SaveDurable(candidate, ownedVaultSession);
                }
                _store.NoteCommittedExternally();

                if (!ReferenceEquals(_configuration, ownedConfig) ||
                    ownedVaultSession != VaultDatabase.Current.SessionGeneration)
                {
                    AppendLog("Website create committed, but ownership changed before the editor refreshed.");
                    return;
                }

                ownedConfig.Workflows.Add(next);
                if (firstWebsite)
                {
                    ownedConfig.FirstWebsiteGuidanceShown = true;
                    ownedConfig.ShortcutTipShown = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The new website could not be saved: " + RunIssueNotice.SafeExceptionReason(ex),
                    "Create website", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            RefreshWorkflowList(next);
            AppendLog($"Created website '{next.Name}' with " +
                (next.AutoLogin ? "an encrypted login." : "no automatic login."));
            // No repeated confirmation modal; the new website is selected and
            // Test this site is immediately available.
            if (firstWebsite)
                MessageBox.Show(this,
                    WebsiteNextStepGuidance.ForCreatedWebsite(next.Name, next.AutoLogin, true),
                    WebsiteNextStepGuidance.DialogTitle(true),
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open the new website window: " + RunIssueNotice.SafeExceptionReason(ex),
                "Create workflow", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            wizard.ClearPendingSecrets();
            if (ReferenceEquals(_newWorkflowWindow, wizard)) _newWorkflowWindow = null;
        }
    }

    private void DuplicateWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (_editorSession.Selection is null || _vaultLockInProgress || _windowsLocked || _exiting ||
            !VaultDatabase.Current.IsUnlocked) return;
        try { ApplyCurrentEditor(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, RunIssueNotice.SafeExceptionReason(ex), "Cannot duplicate", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ownedConfig = _configuration;
        var ownedVaultSession = VaultDatabase.Current.SessionGeneration;
        var next = _editorSession.Selection.CloneWithoutCredential();
        try
        {
            var candidate = System.Text.Json.JsonSerializer.Deserialize<Configuration>(
                System.Text.Json.JsonSerializer.Serialize(ownedConfig))
                ?? throw new InvalidOperationException("Unable to prepare the duplicate snapshot.");
            candidate.Workflows.Add(next);
            if (!ReferenceEquals(_configuration, ownedConfig) ||
                !VaultDatabase.Current.IsUnlocked ||
                ownedVaultSession != VaultDatabase.Current.SessionGeneration)
                throw new InvalidOperationException("The vault or configuration changed before duplicate finished.");
            _store.SaveDurable(candidate, ownedVaultSession);
            _store.NoteCommittedExternally();
            if (!ReferenceEquals(_configuration, ownedConfig) ||
                ownedVaultSession != VaultDatabase.Current.SessionGeneration)
            {
                AppendLog("Website duplicate committed, but ownership changed before the editor refreshed.");
                return;
            }
            ownedConfig.Workflows.Add(next);
            RefreshWorkflowList(next);
            if (!_editorSession.IsDirty) MarkSaved();
            AppendLog($"Duplicated '{next.Name}' without a password (add credentials separately). Saved.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the duplicated website: " + RunIssueNotice.SafeExceptionReason(ex),
                "Duplicate website", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (_editorSession.Selection is null || _vaultLockInProgress || _windowsLocked || _exiting ||
            !VaultDatabase.Current.IsUnlocked) return;
        var w = _editorSession.Selection;
        // Capture authorization continuity before the modal pumps the dispatcher.
        var ownedConfig = _configuration;
        var ownedVaultSession = VaultDatabase.Current.SessionGeneration;
        var ownedRevision = _workspaceRevision;
        if (!ownedConfig.Workflows.Any(item => ReferenceEquals(item, w))) return;
        if (MessageBox.Show(this, $"Delete '{w.Name}' and its encrypted credential?", "Delete workflow",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        try
        {
            if (!ReferenceEquals(_editorSession.Selection, w) ||
                !ReferenceEquals(_configuration, ownedConfig) ||
                !ownedConfig.Workflows.Any(item => ReferenceEquals(item, w)) ||
                ownedRevision != _workspaceRevision ||
                !VaultDatabase.Current.IsUnlocked ||
                ownedVaultSession != VaultDatabase.Current.SessionGeneration)
            {
                AppendLog("Website delete canceled: selection or vault changed during confirmation.");
                return;
            }

            var candidate = System.Text.Json.JsonSerializer.Deserialize<Configuration>(
                System.Text.Json.JsonSerializer.Serialize(ownedConfig))
                ?? throw new InvalidOperationException("Unable to prepare the delete snapshot.");
            // Identity-bound removal: never delete by bare GUID if the owned
            // instance is no longer a member of this configuration.
            if (candidate.Workflows.Count(workflow => workflow.Id == w.Id) != 1)
            {
                AppendLog("Website delete canceled: workflow identity is no longer unique in the snapshot.");
                return;
            }
            candidate.Workflows.RemoveAll(workflow => workflow.Id == w.Id);
            foreach (var workspace in candidate.Workspaces)
                workspace.WorkflowIds.RemoveAll(id => id == w.Id);

            // Clear selection before commit so a pending password cannot retarget
            // the website we are about to remove from cold storage.
            _loadingEditor = true;
            WorkflowList.SelectedItem = null;
            _editorSession.Select(null);
            _loadingEditor = false;

            _vault.DeleteWorkflowWithConfiguration(w.Id, candidate, ownedVaultSession);
            _store.NoteCommittedExternally();

            if (!ReferenceEquals(_configuration, ownedConfig) ||
                ownedVaultSession != VaultDatabase.Current.SessionGeneration)
            {
                AppendLog("Website delete committed, but ownership changed before the editor refreshed.");
                return;
            }

            ownedConfig.Workflows.RemoveAll(workflow => workflow.Id == w.Id);
            foreach (var workspace in ownedConfig.Workspaces)
                workspace.WorkflowIds.RemoveAll(id => id == w.Id);
            RefreshWorkflowList(ownedConfig.Workflows.FirstOrDefault());
            if (!_editorSession.IsDirty) MarkSaved();
            AppendLog("Website and encrypted credential deleted.");
        }
        catch (Exception ex)
        {
            AppendLog("Website delete failed: " + ex.GetType().Name);
            MessageBox.Show(this,
                "Could not delete the website. Your vault was left unchanged if the encrypted write failed.",
                "Delete website", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshWorkflowList(_configuration.Workflows.FirstOrDefault(workflow => workflow.Id == w.Id)
                ?? _configuration.Workflows.FirstOrDefault());
        }
    }

}
