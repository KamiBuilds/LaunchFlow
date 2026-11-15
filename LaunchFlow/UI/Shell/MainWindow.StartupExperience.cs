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

/// <summary>First-run guidance, welcome screens, optional update status and Windows Hello UI.</summary>
public partial class MainWindow
{
    private void EnsureStartupWorkflowVisible()
    {
        // This view uses a headerless WPF TabControl. WPF can leave its initial
        // SelectedContent unrealized until the first tab change, even though
        // the workflow collection was already loaded from the encrypted vault.
        // Re-select Workflows AFTER Loaded, when the control template exists.
        // Suppress normal tab-change save/password handling during this one-time
        // initial render: no user edit has happened yet.
        if (_startupWorkflowViewReady) return;
        _startupWorkflowViewReady = true;
        _revertingTab = true;
        try
        {
            MainTabs.ApplyTemplate();
            MainTabs.SelectedIndex = -1;
            MainTabs.SelectedIndex = 0;
        }
        finally
        {
            _revertingTab = false;
        }

        // The first saved workflow should immediately populate the editor.
        // Suppress selection handlers until its controls have been initialized.
        var selected = WorkflowList.SelectedItem as WebsiteWorkflow
                       ?? _configuration.Workflows.FirstOrDefault();
        _loadingEditor = true;
        try
        {
            WorkflowList.SelectedItem = selected;
            _editorSession.Select(selected);
        }
        finally
        {
            _loadingEditor = false;
        }
        if (selected is null) ShowEmptyEditor();
        else ShowEditor(selected);
        SyncTopNavigation();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Vault creation, migration and unlock happen in App.OnStartup,
        // before this window exists and before any startup automation.
        RunOptionalStartupUiStep(() => DarkWindowChrome.Apply(this));
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(HotkeyHook);
        RunOptionalStartupUiStep(() => TrySetShortcut(
            string.IsNullOrWhiteSpace(_configuration.GlobalShortcut) ? "Ctrl+M" : _configuration.GlobalShortcut));
        // A malformed legacy workflow or editor selection must not prevent the
        // user from getting the main window open to inspect and fix it.
        try { EnsureStartupWorkflowVisible(); }
        catch (Exception ex)
        {
            StartupDiagnostics.Record(StartupDiagnostics.Stage.OptionalUiInitializationFailed, ex);
        }
        if (_configuration.RunOnAppStart) Dispatcher.BeginInvoke(new Action(() => _ = RunEnabledAsync()));
        // --tray is handled in App.OnStartup to avoid a window flash; keep this
        // as a fallback if the window was shown before the flag was applied.
        if (Environment.GetCommandLineArgs().Contains("--tray", StringComparer.OrdinalIgnoreCase)
            && IsVisible)
        {
            ShowInTaskbar = false;
            Hide();
            ShowInTaskbar = true;
        }
        RunOptionalStartupUiStep(() => AppendLog(BrowserEnvironment.Probe().Summary));
        // A second launch might have arrived during the first-run setup wizard.
        ((App)System.Windows.Application.Current).NotifyMainWindowReady(this);
        Dispatcher.BeginInvoke(new Action(() => RunOptionalStartupUiStep(MaybeShowWelcomeChecklist)));
        Dispatcher.BeginInvoke(new Action(() => _ = SafeStartupUpdateCheckAsync()));
    }

    private static void RunOptionalStartupUiStep(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            // A failed shortcut, tutorial, browser probe or initial editor render
            // must not revoke the vault or strand the process behind a fatal popup.
            // Never persist the exception message or browser/profile paths.
            StartupDiagnostics.Record(StartupDiagnostics.Stage.OptionalUiInitializationFailed, ex);
        }
    }

    private async Task SafeStartupUpdateCheckAsync()
    {
        try { await MaybeCheckUpdatesAsync(); }
        catch (Exception ex)
        {
            StartupDiagnostics.Record(StartupDiagnostics.Stage.OptionalUiInitializationFailed, ex);
        }
    }

    private void OpenTutorial(bool firstRunFocus = false)
    {
        if (_tutorial is { IsLoaded: true })
        {
            _tutorial.Activate();
            return;
        }
        _tutorial = new TutorialWindow(firstRunFocus) { Owner = this };
        _tutorial.Closed += (_, _) =>
        {
            _tutorial = null;
            SetGuideCoach(null);
        };
        // Show() is modeless: users can click the actual app controls while following the guide.
        _tutorial.Show();
    }

    private void Help_Click(object sender, RoutedEventArgs e) => OpenTutorial(firstRunFocus: false);

    private void MaybeShowWelcomeChecklist()
    {
        if (_configuration.WelcomeChecklistShown) return;
        if (Environment.GetCommandLineArgs().Contains("--tray", StringComparer.OrdinalIgnoreCase))
            return;

        var ownedConfig = _configuration;
        var ownedVaultSession = VaultDatabase.Current.SessionGeneration;
        if (VaultDatabase.Current.IsUnlocked &&
            VaultDatabase.Current.SessionGeneration == ownedVaultSession &&
            ReferenceEquals(_configuration, ownedConfig))
        {
            ownedConfig.WelcomeChecklistShown = true;
            try { _store.SaveDurable(ownedConfig, ownedVaultSession); }
            catch
            {
                // Keep offering first-run coaching until the preference is durable.
                ownedConfig.WelcomeChecklistShown = false;
            }
        }

        // First unlock: coach only the first-website path so Help feels 1-on-1.
        OpenTutorial(firstRunFocus: true);
    }

    private async void EnableHello_Click(object sender, RoutedEventArgs e)
    {
        if (!VaultDatabase.Current.IsUnlocked)
        {
            MessageBox.Show(this, "Unlock the vault before enabling Windows Hello.",
                "Windows Hello", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var ownedConfig = _configuration;
        var ownedVaultSession = VaultDatabase.Current.SessionGeneration;
        var enableEpoch = _helloUnlock.BeginEnable();
        bool StillOwnsEnable() =>
            _helloUnlock.StillOwnsEnable(enableEpoch, ownedConfig, _configuration, ownedVaultSession,
                VaultDatabase.Current.SessionGeneration, VaultDatabase.Current.IsUnlocked,
                _windowsLocked, _vaultLockInProgress, _exiting);

        if (!await WindowsHelloService.IsSupportedAsync())
        {
            if (!StillOwnsEnable()) return;
            MessageBox.Show(this,
                "Windows Hello is not available on this PC. Set up Windows Hello in Windows Settings first.",
                "Windows Hello", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!StillOwnsEnable()) return;
        var prompt = new SecretPromptWindow("Enable Windows Hello",
            "Enter your LaunchFlow master password once. Next time you can unlock with Windows Hello.")
        { Owner = this };
        if (prompt.ShowDialog() != true) return;
        if (!StillOwnsEnable()) return;
        if (!_master.Verify(prompt.Secret))
        {
            MessageBox.Show(this, "Incorrect master password.", "Windows Hello", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!StillOwnsEnable()) return;
        if (await WindowsHelloService.EnableAsync(prompt.Secret))
        {
            if (!StillOwnsEnable())
            {
                // Reset/lock may have invalidated this enable while Hello UI awaited;
                // remove material strictly so a stale enable cannot leave a recoverable secret.
                try { WindowsHelloService.DisableStrict(); }
                catch (Exception cleanupError)
                {
                    AppendLog("Stale Windows Hello enable cleanup needs attention: " + cleanupError.GetType().Name);
                    try { WindowsHelloService.Disable(); } catch { }
                    if (WindowsHelloService.IsEnabled)
                    {
                        MessageBox.Show(this,
                            "Windows Hello unlock material could not be removed after the enable was cancelled. " +
                            "Delete hello-unlock.bin under %LOCALAPPDATA%\\LaunchFlow, or use Disable Windows Hello after unlocking.",
                            "Windows Hello cleanup incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                return;
            }
            try
            {
                _helloUnlock.TryPersistEnabled(ownedConfig, ownedVaultSession, _store.SaveDurable,
                    WindowsHelloService.Disable);
            }
            catch (Exception persistError)
            {
                AppendLog("Windows Hello unlock preference could not be saved: " + persistError.GetType().Name);
                MessageBox.Show(this,
                    "Windows Hello was set up on this PC, but LaunchFlow could not save the preference. Try again after unlocking the vault.",
                    "Windows Hello", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            AppendLog("Windows Hello unlock enabled for this Windows account.");
            MessageBox.Show(this, "Windows Hello unlock is on for this PC.", "Windows Hello");
        }
    }

    private void DisableHello_Click(object sender, RoutedEventArgs e)
    {
        var ownedConfig = _configuration;
        var ownedVaultSession = VaultDatabase.Current.SessionGeneration;
        if (!_helloUnlock.TryDisableAndPersist(ownedConfig, _configuration, ownedVaultSession,
                VaultDatabase.Current.SessionGeneration, VaultDatabase.Current.IsUnlocked,
                _windowsLocked, _vaultLockInProgress, _exiting,
                WindowsHelloService.Disable, _store.SaveDurable))
        {
            AppendLog("Windows Hello unlock disabled locally; vault was unavailable to persist the preference.");
            return;
        }
        AppendLog("Windows Hello unlock disabled.");
    }

    private async Task MaybeCheckUpdatesAsync()
    {
        try
        {
            var info = await UpdateChecker.CheckAsync(_configuration.UpdateFeedUrl,
                diagnosticsLogEnabled: _configuration.DiagnosticsLogEnabled);
            UpdateStatusLabel.Text = info.Message;
            if (info.UpdateAvailable &&
                UpdateChecker.TryValidateReleasePageUrl(info.ReleaseUrl, out var releasePage) &&
                releasePage is not null)
            {
                var go = MessageBox.Show(this,
                    info.Message + "\n\nOpen the release page?",
                    "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (go == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = releasePage.AbsoluteUri,
                        UseShellExecute = true
                    });
            }
        }
        catch (Exception ex)
        {
            UpdateStatusLabel.Text = "Update check skipped.";
            if (_configuration.DiagnosticsLogEnabled) CrashLog.Write("UpdateCheck", ex);
        }
    }

    private void CheckUpdates_Click(object sender, RoutedEventArgs e) => _ = MaybeCheckUpdatesAsync();

}
