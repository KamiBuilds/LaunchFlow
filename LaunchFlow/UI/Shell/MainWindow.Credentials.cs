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

/// <summary>Explicit credential editing and saving, separate from normal workflow persistence.</summary>
public partial class MainWindow
{
    private void ClearCredential_Click(object sender, RoutedEventArgs e)
    {
        // A modal confirmation pumps WPF events: import or a selection change
        // can replace _editorSession.Selection while the Yes/No prompt is open.
        if (_editorSession.Selection is not { } workflow || _windowsLocked || _vaultLockInProgress ||
            _exiting || !VaultDatabase.Current.IsUnlocked || !CredentialReadAllowed() ||
            !ReferenceEquals(PasswordWebsiteCombo.SelectedItem, workflow)) return;
        // Capture authorization continuity before the modal pumps the dispatcher.
        var revision = _workspaceRevision;
        var ownedConfig = _configuration;
        var ownedVaultSession = VaultDatabase.Current.SessionGeneration;
        if (!CredentialDeleteGuard.IsCurrent(ownedConfig, workflow, workflow,
                revision, revision, true)) return;
        if (MessageBox.Show(this,
                $"Remove the encrypted login password saved for '{workflow.Name}'?",
                "Remove credential", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        if (!CredentialDeleteGuard.IsCurrent(ownedConfig, _editorSession.Selection, workflow,
                revision, _workspaceRevision,
                VaultDatabase.Current.IsUnlocked && !_windowsLocked &&
                !_vaultLockInProgress && !_exiting && CredentialReadAllowed() &&
                ReferenceEquals(PasswordWebsiteCombo.SelectedItem, workflow)) ||
            !ReferenceEquals(_configuration, ownedConfig) ||
            ownedVaultSession != VaultDatabase.Current.SessionGeneration)
        {
            AppendLog("Credential deletion canceled: the website, configuration or vault authorization changed during confirmation.");
            return;
        }

        try
        {
            var index = ownedConfig.Workflows.FindIndex(w => ReferenceEquals(w, workflow));
            if (index < 0) throw new InvalidOperationException("The selected workflow changed.");
            var candidateConfig = System.Text.Json.JsonSerializer.Deserialize<Configuration>(
                System.Text.Json.JsonSerializer.Serialize(ownedConfig))
                ?? throw new InvalidOperationException("Unable to prepare the configuration snapshot.");
            if (candidateConfig.Workflows[index].Id != workflow.Id)
                throw new InvalidOperationException("The selected workflow changed.");
            // Clearing the secret also turns off auto-login / KeePass prefs for
            // this site so a deleted password cannot still be "requested" by Run.
            candidateConfig.Workflows[index].AutoLogin = false;
            candidateConfig.Workflows[index].KeepassEntryPath = "";
            candidateConfig.Workflows[index].LoginMode = "None";

            if (!CredentialDeleteGuard.IsCurrent(ownedConfig, _editorSession.Selection, workflow,
                    revision, _workspaceRevision,
                    VaultDatabase.Current.IsUnlocked && !_windowsLocked &&
                    !_vaultLockInProgress && !_exiting && CredentialReadAllowed() &&
                    ReferenceEquals(PasswordWebsiteCombo.SelectedItem, workflow)) ||
                !ReferenceEquals(_configuration, ownedConfig) ||
                ownedVaultSession != VaultDatabase.Current.SessionGeneration)
            {
                AppendLog("Credential deletion canceled: the website, configuration or vault authorization changed during confirmation.");
                return;
            }

            _vault.DeleteWithConfiguration(workflow.Id, candidateConfig, ownedVaultSession);
            _store.NoteCommittedExternally();
            if (ReferenceEquals(_configuration, ownedConfig) &&
                ReferenceEquals(_editorSession.Selection, workflow) &&
                revision == _workspaceRevision)
            {
                workflow.AutoLogin = false;
                workflow.KeepassEntryPath = "";
                workflow.LoginMode = "None";
                _loadingEditor = true;
                try
                {
                    AutoLoginCheck.IsChecked = false;
                    if (LoginModeCombo is not null)
                        LoginModeCombo.SelectedIndex = WebsiteLoginMode.Index("None");
                    KeepassEntryBox.Text = "";
                }
                finally { _loadingEditor = false; }
            }
            PasswordInput.Clear();
            UsernameBox.Clear();
            CredentialStatus.Text = "No password saved";
            if (!_editorSession.IsDirty) MarkSaved();
            AppendLog($"Encrypted credential removed for '{workflow.Name}'.");
        }
        catch (Exception ex)
        {
            AppendLog("Credential deletion failed: " + ex.GetType().Name);
            MessageBox.Show(this,
                "Could not delete the credential. The saved login may still be present.",
                "Remove credential", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (_editorSession.Selection is null || !ReferenceEquals(PasswordWebsiteCombo.SelectedItem, _editorSession.Selection) ||
            !_configuration.Workflows.Any(w => ReferenceEquals(w, _editorSession.Selection)))
        {
            MessageBox.Show(this, "Choose a website in the Password Manager before saving credentials.",
                "No workflow selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_vaultLockInProgress || _windowsLocked || !VaultDatabase.Current.IsUnlocked ||
            !PasswordSessionIsValid() || _loadedCredentialWorkflowId != _editorSession.Selection.Id)
        {
            MessageBox.Show(this, "Unlock the Password Manager before saving credentials.",
                "Vault locked", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!CredentialReadAllowed())
        {
            ClearCredentialEditor();
            CredentialStatus.Text = "Credential access is disabled by your organization.";
            MessageBox.Show(this, "Credential access is disabled by your organization. Existing credentials were not changed.",
                "Credentials unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var workflow = _editorSession.Selection;
        var ownedConfig = _configuration;
        var ownedRevision = _workspaceRevision;
        var ownedVaultSession = VaultDatabase.Current.SessionGeneration;
        try
        {
            var existing = _vault.Read(workflow.Id);
            // Only explicitly entered, nonempty values overwrite what is already saved.
            // Saved identities are never reloaded into the editor or exposed on screen.
            var newUsername = UsernameBox.Text.Trim();
            var newPassword = PasswordInput.Password;
            var username = string.IsNullOrEmpty(newUsername) ? existing?.Username ?? "" : newUsername;
            var password = string.IsNullOrEmpty(newPassword) ? existing?.Password ?? "" : newPassword;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                MessageBox.Show(this,
                    "Enter a username/email and a password to save a new login. " +
                    "For an existing login, leave whichever field you do not want to replace blank.",
                    "Credential details needed", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var index = ownedConfig.Workflows.FindIndex(w => ReferenceEquals(w, workflow));
            if (index < 0) throw new InvalidOperationException("The selected workflow changed.");
            var candidateConfig = System.Text.Json.JsonSerializer.Deserialize<Configuration>(
                System.Text.Json.JsonSerializer.Serialize(ownedConfig))
                ?? throw new InvalidOperationException("Unable to prepare the configuration snapshot.");
            if (candidateConfig.Workflows[index].Id != workflow.Id)
                throw new InvalidOperationException("The selected workflow changed.");
            var autoLogin = AutoLoginCheck.IsChecked == true;
            var keepassPath = KeepassEntryBox.Text.Trim();
            candidateConfig.Workflows[index].AutoLogin = autoLogin;
            candidateConfig.Workflows[index].KeepassEntryPath = keepassPath;
            if (!ReferenceEquals(_configuration, ownedConfig) || !ReferenceEquals(_editorSession.Selection, workflow) ||
                !ReferenceEquals(PasswordWebsiteCombo.SelectedItem, workflow) ||
                ownedRevision != _workspaceRevision ||
                !VaultDatabase.Current.IsUnlocked ||
                ownedVaultSession != VaultDatabase.Current.SessionGeneration ||
                _windowsLocked || _vaultLockInProgress || _exiting || !CredentialReadAllowed())
                throw new InvalidOperationException("The workflow or vault changed. Reopen the editor and retry.");

            _vault.SaveWithConfiguration(workflow.Id, new SiteCredential(username, password),
                candidateConfig, ownedVaultSession);
            _store.NoteCommittedExternally();
            if (!ReferenceEquals(_configuration, ownedConfig) || !ReferenceEquals(_editorSession.Selection, workflow) ||
                ownedRevision != _workspaceRevision)
            {
                AppendLog("Credential update committed, but website ownership changed before the editor refreshed.");
                return;
            }
            workflow.AutoLogin = autoLogin;
            workflow.KeepassEntryPath = keepassPath;
            ClearCredentialEditor();
            _loadedCredentialWorkflowId = workflow.Id;
            CredentialStatus.Text = "Encrypted credentials saved";
            AppendLog($"Credentials securely updated for workflow '{workflow.Name}'.");
            MessageBox.Show(this, $"Credentials for '{workflow.Name}' saved in the encrypted vault. " +
                "Other websites and the master password were not changed.",
                "Credentials saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            // Do not include submitted credentials in diagnostics or error messages.
            AppendLog("Credential save failed: " + ex.GetType().Name);
            MessageBox.Show(this, "Could not save credentials. Your existing saved login may still be available. " +
                "Try again or contact support.",
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveAll(showNotification: true);

}
