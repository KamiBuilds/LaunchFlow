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

/// <summary>Website form state, protected password-session lifetime and editor population.</summary>
public partial class MainWindow
{
    private void ShowEmptyEditor()
    {
        _loadingEditor = true;
        // An inactive editor looks like a blank workflow form. Display a true
        // empty-state view until an existing workflow is selected.
        WorkflowEditorScroll.Visibility = Visibility.Collapsed;
        WorkflowEmptyState.Visibility = Visibility.Visible;
        bool noWorkflows = _configuration.Workflows.Count == 0;
        EmptyStateHeading.Text = noWorkflows ? "No websites yet" : "Select a website";
        EmptyStateDescription.Text = noWorkflows
            ? "Start by adding one website and choosing a browser and screen. Login and recorded actions are optional."
            : "Choose one of your saved websites on the left to view its settings, or add another website.";
        CreateFirstWorkflowButton.Content = noWorkflows ? "+ Add your first website" : "+ Add another website";
        DuplicateWorkflowButton.IsEnabled = false;
        DeleteWorkflowButton.IsEnabled = false;
        NameBox.Text = "";
        KeepassEntryBox.Text = "";
        ClearCredentialEditor();
        StepList.ItemsSource = null;
        CredentialStatus.Text = "No website selected";
        PasswordSiteName.Text = "Select a website in the Websites tab";
        _loadingEditor = false;
    }

    private void ShowEditor(WebsiteWorkflow w)
    {
        _loadingEditor = true;
        WorkflowEmptyState.Visibility = Visibility.Collapsed;
        WorkflowEditorScroll.Visibility = Visibility.Visible;
        DuplicateWorkflowButton.IsEnabled = true;
        DeleteWorkflowButton.IsEnabled = true;
        EditorPanel.IsEnabled = true;
        EnabledCheck.IsChecked = w.Enabled;
        NameBox.Text = w.Name;
        StartUrlBox.Text = w.StartUrl;
        DestinationUrlBox.Text = w.DestinationUrl;
        BrowserChoices.Refresh(BrowserCombo, w.Browser);
        // Refresh on selection too: laptop docking may have changed since startup.
        var monitors = MonitorService.GetMonitors();
        MonitorCombo.ItemsSource = monitors;
        MonitorCombo.SelectedItem = MonitorService.Resolve(w, monitors);
        _monitorSelectionChanged = false;
        var resolvedMode = WebsiteLoginMode.Resolve(w.LoginMode, w.AutoLogin);
        LoginModeCombo.SelectedIndex = LoginModeIndex(resolvedMode);
        UsernameContinueBox.Text = w.UsernameContinueSelector;
        PasswordOriginBox.Text = w.PasswordCredentialOrigin;
        AutoLoginCheck.IsChecked = WebsiteLoginMode.UsesSavedPassword(resolvedMode);
        KeepassEntryBox.Text = w.KeepassEntryPath ?? "";
        UpdateKeePassEntryLabel();
        UsernameSelectorBox.Text = w.UsernameSelector;
        PasswordSelectorBox.Text = w.PasswordSelector;
        SubmitSelectorBox.Text = w.SubmitSelector;
        CredentialOriginBox.Text = w.CredentialOrigin;
        LoginSuccessSelectorBox.Text = w.LoginSuccessSelector;
        LoginSuccessUrlBox.Text = w.LoginSuccessUrlContains;
        LoginWaitBox.Text = w.LoginWaitSeconds.ToString();
        ClearCredentialEditor();
        PasswordSiteName.Text = w.Name;
        CredentialStatus.Text = DescribeCredentialStatus(w);
        if (MainTabs.SelectedIndex == 1 && SettingsTabs.SelectedIndex == 1 && PasswordSessionIsValid()) LoadCredentialEditor(w);
        StepList.ItemsSource = w.Steps;
        StepList.Items.Refresh();
        _loadingEditor = false;
    }

    private void ClearCredentialEditor()
    {
        UsernameBox.Clear();
        PasswordInput.Clear();
        _loadedCredentialWorkflowId = null;
    }

    private void LoadCredentialEditor(WebsiteWorkflow w)
    {
        ClearCredentialEditor();
        if (!CredentialReadAllowed())
        {
            CredentialStatus.Text = "Credential access is disabled by your organization.";
            return;
        }
        try
        {
            // Never put saved usernames or passwords into UI controls, even masked ones.
            // New values are entered only when the user explicitly wants to replace them.
            _loadedCredentialWorkflowId = w.Id;
            CredentialStatus.Text = DescribeCredentialStatus(w);
        }
        catch (Exception ex)
        {
            CredentialStatus.Text = "Credential cannot be decrypted";
            AppendLog("Vault decryption error: " + ex.GetType().Name);
        }
    }

    private bool PasswordSessionIsValid() =>
        _passwordsUnlocked && VaultDatabase.Current.IsUnlocked && DateTimeOffset.UtcNow < _passwordSessionExpiresUtc;

    private void PasswordSessionTimer_Tick(object? sender, EventArgs e)
    {
        if (_passwordsUnlocked && !PasswordSessionIsValid())
            LockPasswordManager("The password manager locked after one hour.");
        else if (PasswordSessionIsValid())
            PasswordSessionLabel.Text = $"Unlocked · password manager tab expires at {_passwordSessionExpiresUtc.ToLocalTime():HH:mm}. Vault relocks after {_configuration.VaultLockMinutes} minutes of Windows lock.";
    }

    private void LockPasswordManager(string reason)
    {
        if (!_passwordsUnlocked) return;
        // Locking switches tabs and clears credential controls. Those internal UI
        // transitions must not create a fresh unsaved website edit after the
        // installer has already durably saved the configuration.
        var wasLoading = _loadingEditor;
        _loadingEditor = true;
        try
        {
            if (!_installerShutdownInProgress)
            {
                try { ApplyCurrentEditor(); }
                catch (Exception ex) { AppendLog("Cannot save credential changes before lock: " + ex.GetType().Name); }
            }
            _passwordsUnlocked = false;
            _passwordSessionExpiresUtc = DateTimeOffset.MinValue;
            ClearCredentialEditor();
            PasswordSessionLabel.Text = "Locked · unlock to manage saved credentials.";
            if (MainTabs.SelectedIndex == 1 && SettingsTabs.SelectedIndex == 1)
            {
                _revertingTab = true;
                try { SettingsTabs.SelectedIndex = 0; }
                finally { _revertingTab = false; }
            }
            AppendLog(reason);
        }
        finally { _loadingEditor = wasLoading; }
    }

}
