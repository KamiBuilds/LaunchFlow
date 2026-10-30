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
            AppendLog("Could not save previous credential: " + ex.Message);
            // Do not switch entries if the previous password could not be encrypted.
            _loadingEditor = true;
            WorkflowList.SelectedItem = _selected;
            _loadingEditor = false;
            return;
        }
        _selected = WorkflowList.SelectedItem as WebsiteWorkflow;
        if (_selected is null) ShowEmptyEditor();
        else ShowEditor(_selected);
    }

    private void RefreshWorkflowList(WebsiteWorkflow? selection)
    {
        _loadingEditor = true;
        WorkflowList.Items.Refresh();
        WorkflowList.SelectedItem = selection;
        _selected = selection;
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
            MessageBox.Show(this, ex.Message, "Cannot save previous website", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            var firstWebsite = _configuration.Workflows.Count == 0 &&
                !_configuration.FirstWebsiteGuidanceShown;
            var previousShortcutTip = _configuration.ShortcutTipShown;
            var previousFirstGuidance = _configuration.FirstWebsiteGuidanceShown;
            try
            {
                // The encrypted credentials and website settings must persist
                // before showing any nonessential UI message.
                if (!string.IsNullOrEmpty(wizard.NewPassword))
                    _vault.Save(next.Id, new(wizard.NewUsername, wizard.NewPassword));
                _configuration.Workflows.Add(next);
                if (firstWebsite)
                {
                    _configuration.FirstWebsiteGuidanceShown = true;
                    _configuration.ShortcutTipShown = true; // avoid a second first-use dialog on Save
                }
                _store.SaveDurable(_configuration);
            }
            catch (Exception ex)
            {
                _configuration.Workflows.Remove(next);
                _configuration.FirstWebsiteGuidanceShown = previousFirstGuidance;
                _configuration.ShortcutTipShown = previousShortcutTip;
                try { _vault.Delete(next.Id); } catch { /* preserve recovery on partial storage error */ }
                MessageBox.Show(this, "The new website could not be saved: " + ex.Message,
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
            MessageBox.Show(this, "Could not open the new website window: " + ex.Message,
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
        if (_selected is null) return;
        ApplyCurrentEditor();
        var next = _selected.CloneWithoutCredential();
        _configuration.Workflows.Add(next);
        RefreshWorkflowList(next);
        MarkUnsavedChanges();
        AppendLog("Workflow duplicated without a password (add credentials separately).");
    }

    private void DeleteWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var w = _selected;
        if (MessageBox.Show(this, $"Delete '{w.Name}' and its encrypted credential?", "Delete workflow",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        // Clear selection first so a pending password is not written to a deleted workflow.
        _loadingEditor = true;
        WorkflowList.SelectedItem = null;
        _configuration.Workflows.Remove(w);
        _vault.Delete(w.Id);
        _selected = null;
        _loadingEditor = false;
        RefreshWorkflowList(_configuration.Workflows.FirstOrDefault());
        _store.Save(_configuration);
        AppendLog("Workflow and encrypted credential deleted.");
    }

}
