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
    // A synchronous save owns exactly one edit generation and live configuration.
    // No UI/control references enter the draft or encrypted persistence boundary.
    private long _editGeneration;
    private bool _saveInProgress;

    private ConfigurationEditDraft CaptureConfigurationDraft(bool showNotification)
    {
        int delay = int.TryParse(DelayBox.Text, out int d) && d >= 0 && d <= 300
            ? d : throw new ArgumentException("Unlock delay must be between 0 and 300 seconds.");
        int minutes = int.TryParse(VaultTimeoutBox.Text, out int m) && m >= 1 && m <= 120
            ? m : throw new ArgumentException("Vault lock timeout must be between 1 and 120 minutes.");

        WebsiteEditDraft? website = null;
        if (_selected is not null && !_loadingEditor)
        {
            var index = _configuration.Workflows.FindIndex(w => ReferenceEquals(w, _selected));
            if (index < 0)
                throw new InvalidOperationException("Website was replaced or removed; save the current selection again.");
            var browser = BrowserSelection(BrowserCombo);
            if (string.IsNullOrWhiteSpace(browser))
                throw new InvalidOperationException("Choose an installed browser before saving this website.");
            var monitor = MonitorCombo.SelectedItem as MonitorChoice;
            var replaceMonitor = monitor is not null &&
                (_monitorSelectionChanged || string.IsNullOrWhiteSpace(_selected.MonitorDeviceName));
            website = new WebsiteEditDraft(
                index, _selected.Id,
                EnabledCheck.IsChecked == true,
                string.IsNullOrWhiteSpace(NameBox.Text) ? "Unnamed website" : NameBox.Text.Trim(),
                StartUrlBox.Text.Trim(), DestinationUrlBox.Text.Trim(), browser,
                replaceMonitor, monitor?.Index ?? _selected.MonitorIndex,
                monitor?.DeviceName ?? _selected.MonitorDeviceName,
                SelectedLoginMode(LoginModeCombo.SelectedIndex),
                UsernameContinueBox.Text.Trim(), PasswordOriginBox.Text.Trim(),
                KeepassEntryBox.Text.Trim(), UsernameSelectorBox.Text.Trim(),
                PasswordSelectorBox.Text.Trim(), SubmitSelectorBox.Text.Trim(),
                CredentialOriginBox.Text.Trim(), LoginSuccessSelectorBox.Text.Trim(),
                LoginSuccessUrlBox.Text.Trim(),
                int.TryParse(LoginWaitBox.Text, out var seconds)
                    ? Math.Clamp(seconds, 5, 180) : 60);
        }
        var tip = showNotification && !_configuration.ShortcutTipShown &&
            _configuration.Workflows.Any(w => w.Enabled &&
                !string.Equals(w.StartUrl, "https://example.com", StringComparison.OrdinalIgnoreCase));
        return new ConfigurationEditDraft(
            website, (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto",
            StartupCheck.IsChecked == true, UnlockCheck.IsChecked == true,
            LaunchCheck.IsChecked == true, TrayCheck.IsChecked == true,
            NotifyCheck.IsChecked == true, SealProfilesCheck.IsChecked == true,
            TaskSchedulerCheck.IsChecked == true,
            string.IsNullOrWhiteSpace(UpdateFeedBox.Text) ? null : UpdateFeedBox.Text.Trim(),
            delay, minutes, ShortcutBox.Text, KeePassDbBox.Text.Trim(),
            KeePassKeyBox.Text.Trim(), _configuration.ShortcutTipShown || tip);
    }

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
        if (_loadingEditor || _configuration is null || _exiting) return;
        _editGeneration++;
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
        if (_saveInProgress || _vaultLockInProgress || !VaultDatabase.Current.IsUnlocked)
            return false;

        _saveInProgress = true;
        try
        {
            var originalConfig = _configuration;
            var originalSelection = _selected;
            var originalRevision = _editGeneration;
            var vaultGeneration = VaultDatabase.Current.SessionGeneration;

            // UI data are captured into an immutable draft. Nothing writes to
            // live workflows until validation AND encrypted storage succeed.
            var draft = CaptureConfigurationDraft(showNotification);
            bool Owns() =>
                ReferenceEquals(_configuration, originalConfig) &&
                ReferenceEquals(_selected, originalSelection) &&
                (originalSelection is null || originalConfig.Workflows.Any(w =>
                    ReferenceEquals(w, originalSelection))) &&
                _editGeneration == originalRevision &&
                !_vaultLockInProgress && !_windowsLocked && !_exiting &&
                VaultDatabase.Current.IsUnlocked &&
                VaultDatabase.Current.SessionGeneration == vaultGeneration;

            var result = _saveTransaction.Commit(originalConfig, draft.Apply,
                Owns, vaultGeneration, draft.Apply);
            if (!result.Published)
            {
                // Some outcomes have already committed a previous revision.
                // Never acknowledge newer edits or turn a post-write conflict
                // into a false rollback claim.
                if (result.State == SaveTransactionState.CommittedButSuperseded)
                {
                    AppendLog("An earlier edit revision was saved; newer changes remain unsaved.");
                    return false;
                }
                if (result.State == SaveTransactionState.CommittedButPublicationFailed)
                {
                    AppendLog("Configuration was committed but not published in the UI.");
                    ShowAutoSaveFailed();
                    return false;
                }
                if (result.Error is not null)
                    throw result.Error;
                return false; // superseded before the write
            }

            MarkSaved();
            // Platform/UI effects are NOT part of the storage transaction.
            // Their failures must not misreport a successful encrypted commit.
            try
            {
                var preference = (_configuration.StartWithWindows,
                    _configuration.PreferTaskSchedulerAutostart);
                if (_lastAppliedStartupRegistration != preference)
                {
                    _startupRegistration.Apply(preference.StartWithWindows,
                        preference.PreferTaskSchedulerAutostart);
                    _lastAppliedStartupRegistration = preference;
                }
            }
            catch (Exception ex)
            {
                AppendLog("Configuration saved, but Windows startup registration failed: " +
                    ex.GetType().Name);
                SaveStatusLabel.Text = "Saved · Startup setup failed";
            }

            var wasLoading = _loadingEditor;
            try
            {
                _loadingEditor = true;
                ApplyUiLanguage();
                AutoLoginCheck.IsChecked = draft.Website is not null &&
                    WebsiteLoginMode.UsesSavedPassword(draft.Website.LoginMode);
                RefreshKeePassStatus();
                WorkflowList.Items.Refresh();
                WorkflowList.SelectedItem = _selected;
            }
            catch (Exception ex)
            {
                AppendLog("Configuration saved; presentation refresh failed: " + ex.GetType().Name);
            }
            finally
            {
                _loadingEditor = wasLoading;
            }
            if (showNotification) AppendLog("Settings and workflows saved.");
            if (showNotification && !_configuration.ShortcutTipShown == false &&
                !originalConfig.ShortcutTipShown && draft.ShortcutTipShown)
            {
                // ShortcutTipShown is persisted in the original encrypted commit.
                // This optional dialog may reenter WPF and generate a NEW dirty edit.
                try
                {
                    MessageBox.Show(this, $"Your workspace is ready!\\n\\nPress {ShortcutBox.Text} any time LaunchFlow is running to open your enabled websites.\\n\\nYou can change this in Settings → Shortcuts.",
                        "Run your workspace", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex) { AppendLog("Shortcut guidance failed: " + ex.GetType().Name); }
            }
            return true;
        }
        catch (Exception ex)
        {
            // Never conflate a failed save with an in-memory edit; retry remains possible.
            ShowAutoSaveFailed();
            if (!_autoSaving)
                MessageBox.Show(this, ex.Message, "Cannot save LaunchFlow",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                AppendLog("Automatic save failed: " + ex.GetType().Name);
            return false;
        }
        finally { _saveInProgress = false; }
    }

}
