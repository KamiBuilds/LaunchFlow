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

/// <summary>Legacy workspace selection and workspace management.</summary>
public partial class MainWindow
{
    private void WorkspaceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorkspaceCombo is null || WorkspaceNameBox is null) return;
        if (WorkspaceCombo.SelectedItem is WorkspaceProfile profile)
            WorkspaceNameBox.Text = profile.Name;
        if (_configuration is not null) UpdateRunButtons();
    }

    private void WorkspaceNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_configuration is null) return;
        if (WorkspaceCombo.SelectedItem is WorkspaceProfile profile &&
            !WorkspaceCatalog.IsCurrentSelection(_configuration, profile, WorkspaceNameBox.Text))
            WorkspaceCombo.SelectedItem = null;
        UpdateRunButtons();
    }

    private void SaveWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var name = WorkspaceNameBox.Text.Trim();
        if (name.Length is < 1 or > 64)
        {
            MessageBox.Show(this, "Enter a workspace name (1–64 characters).", "Workspace",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!SaveAll()) return;

        var existing = WorkspaceCatalog.FindByName(_configuration, name);
        var revision = _workspaceRevision;
        var savedWorkflowIds = _configuration.Workflows.Where(workflow => workflow.Enabled)
            .Select(workflow => workflow.Id).ToArray();
        if (existing is not null && MessageBox.Show(this,
            "Replace the workflow selection in this workspace?", "Update workspace",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        // Modal confirmations pump UI events: import may replace the saved configuration.
        if (_windowsLocked || _vaultLockInProgress || _exiting || !VaultDatabase.Current.IsUnlocked ||
            !WorkspaceCatalog.ConfirmationStillValid(
                _configuration, name, existing, revision, _workspaceRevision, savedWorkflowIds))
        {
            MessageBox.Show(this, "Workspace data changed while confirmation was open. Review the current workspaces and save again.",
                "Workspace changed", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        WorkspaceProfile profile;
        try
        {
            profile = WorkspaceCatalog.Save(_configuration, name,
                () => _store.SaveDurable(_configuration));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Workspace not saved",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _workspaceRevision++;
        WorkspaceCombo.Items.Refresh();
        WorkspaceCombo.SelectedItem = profile;
        WorkspaceNameBox.Text = profile.Name;
    }

    private void PreviewWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceCombo.SelectedItem is not WorkspaceProfile selected ||
            !WorkspaceCatalog.IsCurrentSelection(_configuration, selected, WorkspaceNameBox.Text))
        {
            MessageBox.Show(this, "Select a saved workspace first.");
            return;
        }
        new WorkspacePreviewWindow(selected, _configuration) { Owner = this }.Show();
    }

    private void RunWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceCombo.SelectedItem is not WorkspaceProfile profile ||
            !WorkspaceCatalog.IsCurrentSelection(_configuration, profile, WorkspaceNameBox.Text))
        {
            MessageBox.Show(this, "Select a saved workspace first.", "Workspace", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _ = RunEnabledAsync(workspace: profile);
    }

}
