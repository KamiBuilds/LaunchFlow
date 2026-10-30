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

/// <summary>Editing, change tracking, validation and durable configuration save.</summary>
public partial class MainWindow
{
    private void ApplyCurrentEditor()
    {
        if (_selected is null || _loadingEditor) return;
        var w = _selected;
        w.Enabled = EnabledCheck.IsChecked == true;
        w.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Unnamed website" : NameBox.Text.Trim();
        w.StartUrl = StartUrlBox.Text.Trim();
        w.DestinationUrl = DestinationUrlBox.Text.Trim();
        var selectedBrowser = BrowserSelection(BrowserCombo);
        if (string.IsNullOrWhiteSpace(selectedBrowser))
            throw new InvalidOperationException("Choose an installed browser before saving this website.");
        w.Browser = selectedBrowser;
        if (MonitorCombo.SelectedItem is MonitorChoice monitor &&
            (_monitorSelectionChanged || string.IsNullOrWhiteSpace(w.MonitorDeviceName)))
        {
            w.MonitorIndex = monitor.Index;
            w.MonitorDeviceName = monitor.DeviceName;
        }
        w.LoginMode = SelectedLoginMode(LoginModeCombo.SelectedIndex);
        w.UsernameContinueSelector = UsernameContinueBox.Text.Trim();
        w.PasswordCredentialOrigin = PasswordOriginBox.Text.Trim();
        w.AutoLogin = WebsiteLoginMode.UsesSavedPassword(w.LoginMode);
        AutoLoginCheck.IsChecked = w.AutoLogin;
        w.KeepassEntryPath = KeepassEntryBox.Text.Trim();
        w.UsernameSelector = UsernameSelectorBox.Text.Trim();
        w.PasswordSelector = PasswordSelectorBox.Text.Trim();
        w.SubmitSelector = SubmitSelectorBox.Text.Trim();
        w.CredentialOrigin = CredentialOriginBox.Text.Trim();
        w.LoginSuccessSelector = LoginSuccessSelectorBox.Text.Trim();
        w.LoginSuccessUrlContains = LoginSuccessUrlBox.Text.Trim();
        w.LoginWaitSeconds = int.TryParse(LoginWaitBox.Text, out int seconds) ? Math.Clamp(seconds, 5, 180) : 60;

        // Credentials are NOT implicitly saved by Save All, tab navigation, or shutdown.
        // The Settings → Passwords category has an explicit Save credentials button. This avoids
        // accidentally overwriting a working login when switching workflows.
    }

    private void MarkUnsavedChanges()
    {
        if (_loadingEditor || _configuration is null || _exiting || _autoSaving) return;
        _autoSaveSchedule.Changed(DateTimeOffset.UtcNow);
        _autoSaveTimer.Start();
        _hasUnsavedChanges = true;
        SaveStatusLabel.Text = "Unsaved changes";
        SaveStatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(255, 202, 111));
        SaveChangesButton.FontWeight = FontWeights.Bold;
        SaveChangesButton.Content = "Save changes";
        SaveChangesButton.ToolTip = "Changes are waiting for encrypted automatic save. Click to save immediately.";
    }

    private void MarkSaved()
    {
        _autoSaveSchedule.Saved();
        _autoSaveTimer.Stop();
        _hasUnsavedChanges = false;
        SaveStatusLabel.Text = "All changes saved";
        SaveStatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(133, 201, 185));
        SaveChangesButton.FontWeight = FontWeights.Normal;
        SaveChangesButton.Content = "Save changes";
        SaveChangesButton.ToolTip = "Latest changes saved to the encrypted vault";
    }

    private bool SaveAll(bool showNotification = false)
    {
        if (_vaultLockInProgress || !VaultDatabase.Current.IsUnlocked) return false;
        try
        {
            ApplyCurrentEditor();
            _configuration.UiLanguage = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
            ApplyUiLanguage();
            _configuration.StartWithWindows = StartupCheck.IsChecked == true;
            _configuration.RunOnUnlock = UnlockCheck.IsChecked == true;
            _configuration.RunOnAppStart = LaunchCheck.IsChecked == true;
            _configuration.MinimizeToTray = TrayCheck.IsChecked == true;
            _configuration.TrayNotificationsEnabled = NotifyCheck.IsChecked == true;
            _configuration.SealBrowserProfilesOnLock = SealProfilesCheck.IsChecked == true;
            _configuration.DiagnosticsLogEnabled = false;
            _configuration.PreferTaskSchedulerAutostart = TaskSchedulerCheck.IsChecked == true;
            _configuration.UpdateFeedUrl = string.IsNullOrWhiteSpace(UpdateFeedBox.Text)
                ? null
                : UpdateFeedBox.Text.Trim();
            if (!int.TryParse(DelayBox.Text, out int delay) || delay < 0 || delay > 300)
                throw new ArgumentException("Unlock delay must be between 0 and 300 seconds.");
            _configuration.UnlockDelaySeconds = delay;
            if (!int.TryParse(VaultTimeoutBox.Text, out int vaultMinutes) || vaultMinutes < 1 || vaultMinutes > 120)
                throw new ArgumentException("Vault lock timeout must be between 1 and 120 minutes.");
            _configuration.VaultLockMinutes = vaultMinutes;
            _configuration.GlobalShortcut = ShortcutBox.Text;
            _configuration.KeePassDatabasePath = KeePassDbBox.Text.Trim();
            _configuration.KeePassKeyFilePath = KeePassKeyBox.Text.Trim();
            RefreshKeePassStatus();
            // The service validates every website before durable persistence.
            // UI never signals a completed save unless that boundary succeeds.
            _workflowConfiguration.Save(_configuration);
            MarkSaved();
            try
            {
                var preference = (_configuration.StartWithWindows,
                    _configuration.PreferTaskSchedulerAutostart);
                if (_lastAppliedStartupRegistration != preference)
                {
                    _startupRegistration.Apply(preference.StartWithWindows, preference.PreferTaskSchedulerAutostart);
                    _lastAppliedStartupRegistration = preference;
                }
            }
            catch (Exception ex)
            {
                AppendLog("Windows startup preference could not be applied: " + ex.Message);
            }
            _loadingEditor = true;
            WorkflowList.Items.Refresh();
            WorkflowList.SelectedItem = _selected;
            _loadingEditor = false;
            if (showNotification) AppendLog("Settings and workflows saved.");
            if (showNotification && !_configuration.ShortcutTipShown &&
                _configuration.Workflows.Any(w => w.Enabled && !string.Equals(w.StartUrl, "https://example.com", StringComparison.OrdinalIgnoreCase)))
            {
                _configuration.ShortcutTipShown = true;
                _store.Save(_configuration);
                MessageBox.Show(this, $"Your workspace is ready!\n\nPress {ShortcutBox.Text} any time LaunchFlow is running to open your enabled websites.\n\nYou can change this in Settings → Shortcuts.",
                    "Run your workspace", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return true;
        }
        catch (Exception ex)
        {
            // Debounced saves must never pop up a modal dialog on every invalid
            // keystroke. Preserve the dirty state and allow explicit retry.
            if (!_autoSaving)
            {
                ShowAutoSaveFailed();
                MessageBox.Show(this, ex.Message, "Cannot save LaunchFlow",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                AppendLog("Automatic save could not complete: " + ex.GetType().Name);
            }
            return false;
        }
    }

}
