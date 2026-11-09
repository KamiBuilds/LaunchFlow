using LaunchFlow.Models;
using LaunchFlow.Services;
using System.Windows;
namespace LaunchFlow;
public sealed partial class MainWindow
{
    private void ImportLaunchFlowFile(string path)
    {
        if (_runner.IsRunning)
        {
            MessageBox.Show(this, "Stop active workflows before importing.",
                "Import", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!RequireMasterPassword(forcePrompt: true)) return;
        var originalConfiguration = _configuration;
        var capturedRevision = _workspaceRevision;
        var capturedSession = VaultDatabase.Current.SessionGeneration;

        try { SettingsTabs.SelectedIndex = 7; } catch { /* optional */ }
        MainTabs.SelectedIndex = 1; // Settings
        var prompt = new SecretPromptWindow("Open your export",
            "Enter the password used to export this file.")
        { Owner = this };
        if (prompt.ShowDialog() != true) return;
        if (_windowsLocked || _vaultLockInProgress || _exiting || _runner.IsRunning ||
            !VaultDatabase.Current.IsUnlocked ||
            VaultDatabase.Current.SessionGeneration != capturedSession ||
            capturedRevision != _workspaceRevision ||
            !ReferenceEquals(originalConfiguration, _configuration))
        {
            AppendLog("Import canceled: the vault session or configuration changed during password confirmation.");
            return;
        }
        try
        {
            var imported = SecureTransferService.Read(path, prompt.Secret);
            if (MessageBox.Show(this,
                    $"Replace your {_configuration.Workflows.Count} existing workflow(s) with {imported.Config.Workflows.Count} imported workflow(s) and their credentials?\n\n" +
                    "File:\n" + path + "\n\nExisting workflows/passwords will be replaced.",
                    "Confirm import", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            // Modal confirmation allows imports, selection changes and machine
            // lock callbacks. Never publish a stale import after those events.
            if (_windowsLocked || _vaultLockInProgress || _exiting || _runner.IsRunning ||
                !VaultDatabase.Current.IsUnlocked ||
                VaultDatabase.Current.SessionGeneration != capturedSession ||
                capturedRevision != _workspaceRevision ||
                !ReferenceEquals(originalConfiguration, _configuration))
            {
                AppendLog("Import canceled: configuration or vault authorization changed during confirmation.");
                return;
            }

            // One explicit projection is used for the encrypted database,
            // live configuration and every corresponding control. Machine-local
            // startup registration and hotkey bindings remain local.
            var projected = ImportConfigurationProjection.ForThisDevice(
                imported.Config, originalConfiguration, _shortcuts.ActiveShortcut);
            VaultDatabase.Current.ReplaceSnapshot(projected, imported.Credentials, capturedSession);

            _workspaceRevision++;
            _keepassMasterPassword = null;
            _configuration = projected;
            _loadingEditor = true;
            try
            {
                WorkflowList.ItemsSource = _configuration.Workflows;
                WorkspaceCombo.ItemsSource = _configuration.Workspaces;
                WorkspaceCombo.SelectedItem = null;
                WorkspaceNameBox.Clear();
                LanguageCombo.SelectedIndex = _configuration.UiLanguage switch { "en" => 1, "nl" => 2, _ => 0 };
                ApplyUiLanguage();
                StartupCheck.IsChecked = _configuration.StartWithWindows;
                UnlockCheck.IsChecked = _configuration.RunOnUnlock;
                LaunchCheck.IsChecked = _configuration.RunOnAppStart;
                TrayCheck.IsChecked = _configuration.MinimizeToTray;
                NotifyCheck.IsChecked = _configuration.TrayNotificationsEnabled;
                SealProfilesCheck.IsChecked = _configuration.SealBrowserProfilesOnLock;
                TaskSchedulerCheck.IsChecked = _configuration.PreferTaskSchedulerAutostart;
                DelayBox.Text = _configuration.UnlockDelaySeconds.ToString();
                VaultTimeoutBox.Text = _configuration.VaultLockMinutes.ToString();
                ShortcutBox.Text = _configuration.GlobalShortcut;
                KeePassDbBox.Text = _configuration.KeePassDatabasePath ?? "";
                KeePassKeyBox.Text = _configuration.KeePassKeyFilePath ?? "";
                UpdateFeedBox.Text = _configuration.UpdateFeedUrl ?? "";
                RefreshKeePassStatus();
                RefreshWorkflowList(_configuration.Workflows.FirstOrDefault());
            }
            finally { _loadingEditor = false; }
            MarkSaved();
            AppendLog("Imported configuration from " + path);
            MessageBox.Show(this,
                "Import complete. Review your websites, enable the ones you need and click Save.",
                "Import complete");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
