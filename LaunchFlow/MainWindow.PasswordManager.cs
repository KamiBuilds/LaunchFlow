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

/// <summary>Master-password authorization and explicit credential manager controls.</summary>
public partial class MainWindow
{
    private bool _syncingPasswordWebsiteSelection;

    private void SyncPasswordWebsiteSelector()
    {
        if (PasswordWebsiteCombo is null || _configuration is null) return;
        _syncingPasswordWebsiteSelection = true;
        try
        {
            if (!ReferenceEquals(PasswordWebsiteCombo.ItemsSource, _configuration.Workflows))
                PasswordWebsiteCombo.ItemsSource = _configuration.Workflows;
            else
                PasswordWebsiteCombo.Items.Refresh();

            PasswordWebsiteCombo.SelectedItem = _selected;
            PasswordWebsiteCombo.IsEnabled = _configuration.Workflows.Count > 0;
            SaveCredentialsButton.IsEnabled = _selected is not null;
            ClearCredentialButton.IsEnabled = _selected is not null;
        }
        finally { _syncingPasswordWebsiteSelection = false; }
    }

    private void PasswordWebsiteCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPasswordWebsiteSelection || _loadingEditor || _configuration is null ||
            WorkflowList is null || PasswordWebsiteCombo is null || _windowsLocked ||
            _vaultLockInProgress || _exiting || !PasswordSessionIsValid())
            return;

        var target = PasswordWebsiteCombo.SelectedItem as WebsiteWorkflow;
        if (target is null || !_configuration.Workflows.Any(w => ReferenceEquals(w, target)))
        {
            SyncPasswordWebsiteSelector();
            return;
        }
        if (ReferenceEquals(target, _selected)) return;

        var previous = _selected;
        var revision = _workspaceRevision;
        var configuration = _configuration;
        if (UsernameBox.Text.Length > 0 || PasswordInput.Password.Length > 0)
        {
            var answer = MessageBox.Show(this,
                $"Unsaved login changes for '{previous?.Name ?? "the previous website"}' will be discarded. Switch to '{target.Name}'?",
                "Discard unsaved website credentials?", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes ||
                revision != _workspaceRevision ||
                !ReferenceEquals(configuration, _configuration) ||
                !ReferenceEquals(previous, _selected) ||
                !_configuration.Workflows.Any(w => ReferenceEquals(w, target)) ||
                !PasswordSessionIsValid())
            {
                SyncPasswordWebsiteSelector();
                return;
            }
        }

        // Use the existing selected-workflow owner so login mode, KeePass
        // entry, vault ID and the password editor always refer to one website.
        WorkflowList.SelectedItem = target;
        if (!ReferenceEquals(_selected, target)) SyncPasswordWebsiteSelector();
    }

    private bool RequireMasterPassword(bool forcePrompt = false)
    {
        if (_vaultLockInProgress || _windowsLocked || _exiting) return false;
        if (!forcePrompt && PasswordSessionIsValid()) return true;
        if (!_master.IsConfigured) return false;
        var prompt = new UnlockWindow { Owner = this };
        if (prompt.ShowDialog() != true || string.IsNullOrEmpty(prompt.Password)) return false;
        if (_vaultLockInProgress || _windowsLocked || _exiting) return false;
        try
        {
            if (_master.Verify(prompt.Password))
            {
                try { ProfileVaultService.UnsealAll(); }
                catch (Exception ex)
                {
                    AppendLog("Could not unseal browser profiles: " + ex.Message);
                    MessageBox.Show(this, "Some browser sessions could not be restored. Their encrypted files were retained. " +
                        "Affected workflows are blocked; your saved vault logins are separate.",
                        "Browser sessions need attention", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                _passwordsUnlocked = true;
                _passwordSessionExpiresUtc = DateTimeOffset.UtcNow.Add(PasswordSessionDuration);
                PasswordSessionTimer_Tick(null, EventArgs.Empty);
                return true;
            }
        }
        catch (Exception ex) { AppendLog("Master password verification error: " + ex.GetType().Name); }
        MessageBox.Show(this, "Incorrect master password.", "Not unlocked", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private async void LockPasswords_Click(object sender, RoutedEventArgs e)
    {
        await SecureVaultLockAsync("Vault manually locked; master password required before automation.");
    }

    private async void ChangeMasterPassword_Click(object sender, RoutedEventArgs e)
    {
        if (_vaultLockInProgress || _runInProgress || _teachingInProgress || !RequireMasterPassword()) return;
        var current = new SecretPromptWindow("Change master password", "Enter your current master password.") { Owner = this };
        if (current.ShowDialog() != true) return;
        var next = new SecretPromptWindow("New master password",
            $"Choose a new password of at least {PasswordStrength.MinimumLength} characters (mixed case, number, or symbol recommended).")
        { Owner = this };
        if (next.ShowDialog() != true) return;
        if (!PasswordStrength.MeetsMinimum(next.Secret))
        {
            MessageBox.Show(this, PasswordStrength.Evaluate(next.Secret).Hint,
                "Password too weak", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var confirm = new SecretPromptWindow("Confirm new master password", "Enter the same new password again.") { Owner = this };
        if (confirm.ShowDialog() != true) return;
        if (next.Secret != confirm.Secret) { MessageBox.Show(this, "Passwords did not match."); return; }
        try
        {
            _master.Change(current.Secret, next.Secret);
            WindowsHelloService.Disable();
            _configuration.WindowsHelloUnlockEnabled = false;
            await SecureVaultLockAsync("Master password changed; vault locked. Re-enable Windows Hello if you use it.");
            AppendLog("Master password changed. Windows Hello unlock was cleared.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Password not changed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

}
