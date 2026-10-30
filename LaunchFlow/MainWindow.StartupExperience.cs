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
            _selected = selected;
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

    private void OpenTutorial()
    {
        if (_tutorial is { IsLoaded: true })
        {
            _tutorial.Activate();
            return;
        }
        _tutorial = new TutorialWindow { Owner = this };
        _tutorial.Closed += (_, _) => _tutorial = null;
        // Show() is modeless: users can click the actual app controls while following the guide.
        _tutorial.Show();
    }

    private void Help_Click(object sender, RoutedEventArgs e) => OpenTutorial();

    private void MaybeShowWelcomeChecklist()
    {
        if (_configuration.WelcomeChecklistShown) return;
        if (Environment.GetCommandLineArgs().Contains("--tray", StringComparer.OrdinalIgnoreCase))
            return;

        _configuration.WelcomeChecklistShown = true;
        try { _store.Save(_configuration); } catch { /* ignore */ }

        OpenTutorial();
    }

    private async void EnableHello_Click(object sender, RoutedEventArgs e)
    {
        if (!await WindowsHelloService.IsSupportedAsync())
        {
            MessageBox.Show(this,
                "Windows Hello is not available on this PC. Set up Windows Hello in Windows Settings first.",
                "Windows Hello", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var prompt = new SecretPromptWindow("Enable Windows Hello",
            "Enter your LaunchFlow master password once. Next time you can unlock with Windows Hello.")
        { Owner = this };
        if (prompt.ShowDialog() != true) return;
        if (!_master.Verify(prompt.Secret))
        {
            MessageBox.Show(this, "Incorrect master password.", "Windows Hello", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (await WindowsHelloService.EnableAsync(prompt.Secret))
        {
            _configuration.WindowsHelloUnlockEnabled = true;
            _store.Save(_configuration);
            AppendLog("Windows Hello unlock enabled for this Windows account.");
            MessageBox.Show(this, "Windows Hello unlock is on for this PC.", "Windows Hello");
        }
    }

    private void DisableHello_Click(object sender, RoutedEventArgs e)
    {
        WindowsHelloService.Disable();
        _configuration.WindowsHelloUnlockEnabled = false;
        _store.Save(_configuration);
        AppendLog("Windows Hello unlock disabled.");
    }

    private async Task MaybeCheckUpdatesAsync()
    {
        try
        {
            var info = await UpdateChecker.CheckAsync(_configuration.UpdateFeedUrl);
            UpdateStatusLabel.Text = info.Message;
            if (info.UpdateAvailable && !string.IsNullOrWhiteSpace(info.ReleaseUrl))
            {
                var go = MessageBox.Show(this,
                    info.Message + "\n\nOpen the release page?",
                    "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (go == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo { FileName = info.ReleaseUrl, UseShellExecute = true });
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
