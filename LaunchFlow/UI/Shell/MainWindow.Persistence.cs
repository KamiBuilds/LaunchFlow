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
    private bool _saveInProgress;

    private ConfigurationEditDraft CaptureConfigurationDraft(bool showNotification)
    {
        int delay = int.TryParse(DelayBox.Text, out int d) && d >= 0 && d <= 300
            ? d : throw new ArgumentException("Unlock delay must be between 0 and 300 seconds.");
        int minutes = int.TryParse(VaultTimeoutBox.Text, out int m) && m >= 1 && m <= 120
            ? m : throw new ArgumentException("Vault lock timeout must be between 1 and 120 minutes.");

        WebsiteEditDraft? website = null;
        if (_editorSession.Selection is not null && !_loadingEditor)
        {
            var index = _configuration.Workflows.FindIndex(w => ReferenceEquals(w, _editorSession.Selection));
            if (index < 0)
                throw new InvalidOperationException("Website was replaced or removed; save the current selection again.");
            var browser = BrowserSelection(BrowserCombo);
            if (string.IsNullOrWhiteSpace(browser))
                throw new InvalidOperationException("Choose an installed browser before saving this website.");
            var monitor = MonitorCombo.SelectedItem as MonitorChoice;
            var replaceMonitor = monitor is not null &&
                (_monitorSelectionChanged || string.IsNullOrWhiteSpace(_editorSession.Selection.MonitorDeviceName));
            website = new WebsiteEditDraft(
                index, _editorSession.Selection.Id,
                EnabledCheck.IsChecked == true,
                WebsiteEditMapper.NormalizeName(NameBox.Text),
                WebsiteEditMapper.NormalizeText(StartUrlBox.Text),
                WebsiteEditMapper.NormalizeText(DestinationUrlBox.Text), browser,
                replaceMonitor, monitor?.Index ?? _editorSession.Selection.MonitorIndex,
                monitor?.DeviceName ?? _editorSession.Selection.MonitorDeviceName,
                SelectedLoginMode(LoginModeCombo.SelectedIndex),
                WebsiteLoginMode.UsesSavedPassword(SelectedLoginMode(LoginModeCombo.SelectedIndex)),
                WebsiteEditMapper.NormalizeText(UsernameContinueBox.Text),
                WebsiteEditMapper.NormalizeText(PasswordOriginBox.Text),
                WebsiteEditMapper.NormalizeText(KeepassEntryBox.Text),
                WebsiteEditMapper.NormalizeText(UsernameSelectorBox.Text),
                WebsiteEditMapper.NormalizeText(PasswordSelectorBox.Text),
                WebsiteEditMapper.NormalizeText(SubmitSelectorBox.Text),
                WebsiteEditMapper.NormalizeText(CredentialOriginBox.Text),
                WebsiteEditMapper.NormalizeText(LoginSuccessSelectorBox.Text),
                WebsiteEditMapper.NormalizeText(LoginSuccessUrlBox.Text),
                WebsiteEditMapper.NormalizeWait(LoginWaitBox.Text));
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
        if (_editorSession.Selection is null || _loadingEditor) return;
        var w = _editorSession.Selection;
        // Preserve the original staged navigation contract: leading fields are
        // applied before browser validation, and the AutoLogin UI event may run
        // before the later selector inputs are read.
        WebsiteEditMapper.ApplyLeading(w, EnabledCheck.IsChecked == true,
            NameBox.Text, StartUrlBox.Text, DestinationUrlBox.Text);
        var selectedBrowser = BrowserSelection(BrowserCombo);
        var selectedMonitor = MonitorCombo.SelectedItem as MonitorChoice;
        WebsiteEditMapper.ApplyBrowser(w, selectedBrowser,
            selectedMonitor is not null &&
            (_monitorSelectionChanged || string.IsNullOrWhiteSpace(w.MonitorDeviceName)),
            selectedMonitor?.Index ?? w.MonitorIndex,
            selectedMonitor?.DeviceName ?? w.MonitorDeviceName);
        var loginMode = SelectedLoginMode(LoginModeCombo.SelectedIndex);
        WebsiteEditMapper.ApplyLogin(w, loginMode, UsernameContinueBox.Text,
            PasswordOriginBox.Text, WebsiteLoginMode.UsesSavedPassword(loginMode));
        AutoLoginCheck.IsChecked = w.AutoLogin;
        WebsiteEditMapper.ApplyDetails(w, KeepassEntryBox.Text,
            UsernameSelectorBox.Text, PasswordSelectorBox.Text, SubmitSelectorBox.Text,
            CredentialOriginBox.Text, LoginSuccessSelectorBox.Text,
            LoginSuccessUrlBox.Text, WebsiteEditMapper.NormalizeWait(LoginWaitBox.Text));

        // Credentials are NOT implicitly saved by Save All, tab navigation, or shutdown.
        // The Settings → Passwords category has an explicit Save credentials button. This avoids
        // accidentally overwriting a working login when switching workflows.
    }

    private void MarkUnsavedChanges()
    {
        if (_loadingEditor || _configuration is null || _exiting) return;
        _editorSession.MarkEdited();
        _autoSaveSchedule.Changed(DateTimeOffset.UtcNow);
        _autoSaveTimer.Start();
        SaveStatusLabel.Text = "Unsaved changes";
        SaveStatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(255, 202, 111));
        SaveChangesButton.FontWeight = FontWeights.Bold;
        SaveChangesButton.Content = "Save changes";
        SaveChangesButton.ToolTip = "Changes are waiting for encrypted automatic save. Click to save immediately.";
    }

    private void MarkSaved(long? committedRevision = null)
    {
        if (!_editorSession.TryAcknowledge(committedRevision ?? _editorSession.Revision)) return;
        _autoSaveSchedule.Saved();
        _autoSaveTimer.Stop();
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
            var editorOwner = _editorSession.CaptureOwnership();
            var originalSelection = editorOwner.Selection;
            var originalRevision = editorOwner.EditRevision;
            var vaultGeneration = VaultDatabase.Current.SessionGeneration;

            // UI data are captured into an immutable draft. Nothing writes to
            // live workflows until validation AND encrypted storage succeed.
            var draft = CaptureConfigurationDraft(showNotification);
            var showShortcutTip = showNotification &&
                !originalConfig.ShortcutTipShown && draft.ShortcutTipShown;
            bool Owns() =>
                ReferenceEquals(_configuration, originalConfig) &&
                _editorSession.IsCurrent(editorOwner) &&
                (originalSelection is null || originalConfig.Workflows.Any(w =>
                    ReferenceEquals(w, originalSelection))) &&
                !_vaultLockInProgress && !_windowsLocked && !_exiting &&
                VaultDatabase.Current.IsUnlocked &&
                VaultDatabase.Current.SessionGeneration == vaultGeneration;

            // Draft builds/validates the detached candidate; publication must
            // apply the same draft onto the live configuration identity.
            var result = _saveTransaction.Commit(originalConfig, draft.Apply,
                Owns, vaultGeneration, _ => draft.Apply(originalConfig));
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

            // Reentrant selection A -> B -> A with no new edit does not
            // acknowledge an earlier operation merely by matching reference/revision.
            if (_editorSession.IsCurrent(editorOwner)) MarkSaved(originalRevision);
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
                WorkflowList.SelectedItem = _editorSession.Selection;
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
            if (showShortcutTip)
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
                MessageBox.Show(this, RunIssueNotice.SafeExceptionReason(ex), "Cannot save LaunchFlow",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                AppendLog("Automatic save failed: " + ex.GetType().Name);
            return false;
        }
        finally { _saveInProgress = false; }
    }

}
