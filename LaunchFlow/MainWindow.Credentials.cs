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
        // can replace _selected while the Yes/No prompt is open.
        if (_selected is not { } workflow || _windowsLocked || _vaultLockInProgress ||
            _exiting || !VaultDatabase.Current.IsUnlocked || !CredentialReadAllowed()) return;
        var revision = _workspaceRevision;
        if (MessageBox.Show(this,
                $"Remove the encrypted login password saved for '{workflow.Name}'?",
                "Remove credential", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        if (!CredentialDeleteGuard.IsCurrent(_configuration, _selected, workflow,
                revision, _workspaceRevision,
                VaultDatabase.Current.IsUnlocked && !_windowsLocked &&
                !_vaultLockInProgress && !_exiting && CredentialReadAllowed()))
        {
            AppendLog("Credential deletion canceled: the website, configuration or vault authorization changed during confirmation.");
            return;
        }

        try
        {
            // Only the captured identity can be deleted from cold encrypted storage.
            _vault.Delete(workflow.Id);
            PasswordInput.Clear();
            UsernameBox.Clear();
            CredentialStatus.Text = "No password saved";
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
        if (_selected is null)
        {
            MessageBox.Show(this, "Select a workflow before saving credentials.",
                "No workflow selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_vaultLockInProgress || _windowsLocked || !VaultDatabase.Current.IsUnlocked ||
            !PasswordSessionIsValid() || _loadedCredentialWorkflowId != _selected.Id)
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
        try
        {
            var existing = _vault.Read(_selected.Id);
            // Only explicitly entered, nonempty values overwrite what is already saved.
            // Saved identities are never reloaded into the editor or exposed on screen.
            var newUsername = UsernameBox.Password.Trim();
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

            _vault.Save(_selected.Id, new SiteCredential(username, password));
            // Login preference is not a secret; persist it with workflow settings.
            _selected.AutoLogin = AutoLoginCheck.IsChecked == true;
            _selected.KeepassEntryPath = KeepassEntryBox.Text.Trim();
            // Credential writes must never report success for a transient,
            // locked-vault configuration fallback.
            _store.SaveDurable(_configuration);
            ClearCredentialEditor();
            _loadedCredentialWorkflowId = _selected.Id;
            CredentialStatus.Text = "Encrypted credentials saved";
            AppendLog($"Credentials securely updated for workflow '{_selected.Name}'.");
            MessageBox.Show(this, "Credentials saved in the encrypted vault. " +
                "The previous values have been replaced where you entered new ones.",
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
