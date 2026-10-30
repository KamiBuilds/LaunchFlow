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

/// <summary>Import and export presentation, encrypted transfer orchestration.</summary>
public partial class MainWindow
{
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveAll()) return;
        if (!RequireMasterPassword(forcePrompt: true)) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export LaunchFlow",
            Filter = "LaunchFlow file (*.launchflow)|*.launchflow",
            DefaultExt = ".launchflow",
            FileName = "My-LaunchFlow.launchflow"
        };
        if (dialog.ShowDialog(this) != true) return;
        var first = new SecretPromptWindow("Protect your export",
            $"Set an export password ({PasswordStrength.MinimumLength}+ characters). You'll need it on the new PC.")
        { Owner = this };
        if (first.ShowDialog() != true) return;
        var second = new SecretPromptWindow("Confirm export password", "Enter your export password again.") { Owner = this };
        if (second.ShowDialog() != true) return;
        if (first.Secret != second.Secret) { MessageBox.Show(this, "Export passwords did not match."); return; }
        try
        {
            SecureTransferService.Export(dialog.FileName, first.Secret, _configuration, _vault, IncludePasswordsCheck.IsChecked == true);
            AppendLog("Encrypted portable configuration exported.");
            MessageBox.Show(this, "Export saved. Keep the file and password safe.", "Export complete");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_runner.IsRunning) { MessageBox.Show(this, "Stop active workflows before importing."); return; }
        if (!RequireMasterPassword(forcePrompt: true)) return;
        var file = new OpenFileDialog
        {
            Title = "Import LaunchFlow",
            Filter = "LaunchFlow file (*.launchflow)|*.launchflow"
        };
        if (file.ShowDialog(this) != true) return;
        ImportLaunchFlowFile(file.FileName);
    }

    private void ImportLaunchFlowFile(string path)
    {
        if (_runner.IsRunning)
        {
            MessageBox.Show(this, "Stop active workflows before importing.",
                "Import", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!RequireMasterPassword(forcePrompt: true)) return;

        try { SettingsTabs.SelectedIndex = 7; } catch { /* optional */ }
        MainTabs.SelectedIndex = 1; // Settings
        var prompt = new SecretPromptWindow("Open your export",
            "Enter the password used to export this file.")
        { Owner = this };
        if (prompt.ShowDialog() != true) return;
        try
        {
            var imported = SecureTransferService.Read(path, prompt.Secret);
            var originalConfiguration = _configuration;
            var capturedRevision = _workspaceRevision;
            if (MessageBox.Show(this,
                    $"Replace your {_configuration.Workflows.Count} existing workflow(s) with {imported.Config.Workflows.Count} imported workflow(s) and their credentials?\n\n" +
                    "File:\n" + path + "\n\nExisting workflows/passwords will be replaced.",
                    "Confirm import", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            // Modal confirmation allows imports, selection changes and machine
            // lock callbacks. Never publish a stale import after those events.
            if (_windowsLocked || _vaultLockInProgress || _exiting ||
                !VaultDatabase.Current.IsUnlocked ||
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
                imported.Config, originalConfiguration, _activeShortcut);
            VaultDatabase.Current.ReplaceSnapshot(projected, imported.Credentials);

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
