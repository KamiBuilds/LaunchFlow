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

public partial class MainWindow : Window
{
    private readonly ConfigStore _store = new();
    private readonly MasterPasswordService _master = new();
    private TutorialWindow? _tutorial;
    private bool _hasUnsavedChanges;
    private bool _passwordsUnlocked;
    private DateTimeOffset _passwordSessionExpiresUtc = DateTimeOffset.MinValue;
    private Guid? _loadedCredentialWorkflowId;
    private readonly DispatcherTimer _passwordSessionTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private static readonly TimeSpan PasswordSessionDuration = TimeSpan.FromHours(1);
    private bool _windowsLocked;
    private DateTimeOffset? _lockedSinceUtc;
    private CancellationTokenSource? _lockTimeoutCancellation;

    private bool _revertingTab;
    private bool _sidebarCollapsed;
    private bool _teachingInProgress;
    private bool _runInProgress;
    private bool _runStatusActive;
    private bool _vaultLockInProgress;
    private bool _vaultCleanupFailed;
    private bool _installerShutdownInProgress;
    private bool _installerProfileSealFailed;
    private bool _monitorSelectionChanged;
    private long _workspaceRevision;
    private Task _vaultLockTask = Task.CompletedTask;
    private Task<AutomationEngine.RunResult>? _activeRunTask;
    private Task<IPage>? _activeTeachTask;
    private readonly Dictionary<Guid, SignInDecisionWindow> _openSignInDecisions = new();
    private NewWorkflowWindow? _newWorkflowWindow;
    private int _openStepEditors;
    private bool _startupWorkflowViewReady;
    private bool _capturingShortcut;
    private string _activeShortcut = "";

    private readonly CredentialVault _vault;
    private readonly AutomationEngine _runner;
    private Configuration _configuration;
    private WebsiteWorkflow? _selected;
    private bool _loadingEditor;
    private bool _exiting;
    private string? _keepassMasterPassword;
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _unlockCancellation;
    private readonly Forms.NotifyIcon _tray;
    private HwndSource? _windowSource;
    private const int HotkeyId = 0x4C46;
    private const int WmHotkey = 0x0312;
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private static void ConstrainMaximizedWindowToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var values = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        // Native MINMAXINFO uses physical device pixels; WPF's custom title bar
        // otherwise risks covering the taskbar on secondary and scaled monitors.
        values.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        values.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        values.MaxSize.X = info.Work.Right - info.Work.Left;
        values.MaxSize.Y = info.Work.Bottom - info.Work.Top;
        Marshal.StructureToPtr(values, lParam, false);
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public MainWindow()
    {
        InitializeComponent();
        HeaderVersionLabel.Text = "v" + AppInfo.VersionString;
        CurrentVersionLabel.Text = "Installed version " + AppInfo.VersionString;
        try
        {
            _configuration = _store.Load();
            _vault = new CredentialVault();
        }
        catch (Exception ex)
        {
            MessageBox.Show("LaunchFlow cannot open its data safely. The existing files have not been overwritten.\n\n" + ex.Message,
                "LaunchFlow startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
        // The App startup gate already unlocked the encrypted database.
        _passwordsUnlocked = true;
        _passwordSessionExpiresUtc = DateTimeOffset.UtcNow.Add(PasswordSessionDuration);
        VaultTimeoutBox.Text = _configuration.VaultLockMinutes.ToString();
        KeePassDbBox.Text = _configuration.KeePassDatabasePath ?? "";
        KeePassKeyBox.Text = _configuration.KeePassKeyFilePath ?? "";
        SuggestKeePassDatabase();
        RefreshKeePassStatus();
        _runner = new AutomationEngine(_vault);
        _runner.ApproveLoginOriginAsync = ConfirmLoginOriginAsync;
        _runner.OnWorkflowStatus += QueueWorkflowStatus;
        _runner.OnLog += line => Dispatcher.BeginInvoke(new Action(() => AppendLog(line)));

        StartupCheck.IsChecked = _configuration.StartWithWindows;
        UnlockCheck.IsChecked = _configuration.RunOnUnlock;
        LaunchCheck.IsChecked = _configuration.RunOnAppStart;
        DelayBox.Text = _configuration.UnlockDelaySeconds.ToString();
        ShortcutBox.Text = string.IsNullOrWhiteSpace(_configuration.GlobalShortcut) ? "Ctrl+M" : _configuration.GlobalShortcut;
        ShortcutHelpLabel.Text = $"{ShortcutBox.Text} runs all enabled workflows while LaunchFlow is open or hidden in the tray.";
        TrayCheck.IsChecked = _configuration.MinimizeToTray;
        NotifyCheck.IsChecked = _configuration.TrayNotificationsEnabled;
        SealProfilesCheck.IsChecked = _configuration.SealBrowserProfilesOnLock;
        TaskSchedulerCheck.IsChecked = _configuration.PreferTaskSchedulerAutostart;
        UpdateFeedBox.Text = _configuration.UpdateFeedUrl ?? "";
        MonitorCombo.ItemsSource = MonitorService.GetMonitors();
        MonitorCombo.SelectedIndex = 0;
        BrowserChoices.Refresh(BrowserCombo);
        WorkflowList.ItemsSource = _configuration.Workflows;
        WorkspaceCombo.ItemsSource = _configuration.Workspaces;
        LanguageCombo.SelectedIndex = _configuration.UiLanguage switch { "en" => 1, "nl" => 2, _ => 0 };
        ApplyUiLanguage();
        // Track user-visible website configuration edits without inspecting credential values.
        NameBox.TextChanged += (_, _) => MarkUnsavedChanges();
        StartUrlBox.TextChanged += (_, _) => MarkUnsavedChanges();
        EnabledCheck.Checked += (_, _) => MarkUnsavedChanges();
        EnabledCheck.Unchecked += (_, _) => MarkUnsavedChanges();
        BrowserCombo.SelectionChanged += (_, _) => MarkUnsavedChanges();
        MonitorCombo.SelectionChanged += (_, _) => MarkUnsavedChanges();
        KeepassEntryBox.TextChanged += (_, _) => MarkUnsavedChanges();
        foreach (var input in new[] { UsernameContinueBox, PasswordOriginBox,
            UsernameSelectorBox, PasswordSelectorBox, SubmitSelectorBox,
            CredentialOriginBox, LoginSuccessSelectorBox, LoginSuccessUrlBox,
            LoginWaitBox, DelayBox, VaultTimeoutBox, ShortcutBox,
            KeePassDbBox, KeePassKeyBox, UpdateFeedBox })
            input.TextChanged += (_, _) => MarkUnsavedChanges();
        foreach (var toggle in new[] { AutoLoginCheck, StartupCheck, UnlockCheck,
            LaunchCheck, TrayCheck, NotifyCheck, SealProfilesCheck, TaskSchedulerCheck })
        {
            toggle.Checked += (_, _) => MarkUnsavedChanges();
            toggle.Unchecked += (_, _) => MarkUnsavedChanges();
        }
        LoginModeCombo.SelectionChanged += (_, _) => MarkUnsavedChanges();
        LanguageCombo.SelectionChanged += (_, _) => MarkUnsavedChanges();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open LaunchFlow", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Run all enabled websites", null, (_, _) => Dispatcher.Invoke(() => _ = RunEnabledAsync()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitFromTray));
        _tray = new Forms.NotifyIcon
        {
            Text = "LaunchFlow workflow automation",
            Icon = CreateTrayIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        SystemEvents.SessionSwitch += HandleSessionSwitch;
        _passwordSessionTimer.Tick += PasswordSessionTimer_Tick;
        _passwordSessionTimer.Start();
        if (_configuration.Workflows.Count > 0) WorkflowList.SelectedIndex = 0;
        else ShowEmptyEditor();
        AppendLog("LaunchFlow is ready. Encrypted SQLite vault unlocked with Argon2id master key.");
    }

    private void ApplyUiLanguage()
    {
        LocalizationService.ActiveLanguage = _configuration.UiLanguage;
        string Text(string english) => LocalizationService.Text(english, _configuration.UiLanguage);
        WorkflowsNav.Content = Text("Websites");
        SettingsNav.Content = Text("Settings");
        SaveWorkspaceButton.Content = Text("Save");
        PreviewWorkspaceButton.Content = Text("Preview");
        RunWorkspaceButton.Content = "▶ " + Text("Run workspace");
        RunAllButton.Content = "▶ " + Text("Run all");
        StopButton.Content = "■ " + Text("Stop");
        WorkspaceHeading.Text = Text("Workspace profiles").ToUpperInvariant();
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(LocalizationService.ResolveLanguage(_configuration.UiLanguage));
        foreach (var (control, name) in new (DependencyObject Control, string Name)[]
        {
            (WorkflowsNav, "Workflows"), (SettingsNav, "Settings"),
            (SaveWorkspaceButton, "Save workspace"), (RunWorkspaceButton, "Run selected workspace"),
            (RunAllButton, "Run all enabled websites"), (StopButton, "Stop"),
            (WorkspaceCombo, "Saved workspace selector"), (WorkspaceNameBox, "Workspace name"),
            (LanguageCombo, "Application language"),
            (NameBox, "Website name"), (StartUrlBox, "Website start URL"), (MonitorCombo, "Screen"),
            (WorkflowStatusLabel, "Website status"), (EnabledCheck, "Enable or disable this website"),
            (StepList, "Workflow steps after sign-in")
        }) System.Windows.Automation.AutomationProperties.SetName(control, Text(name));
        WorkspaceCombo.ToolTip = Text("Select a saved workspace");
        WorkspaceNameBox.ToolTip = Text("Enter a workspace name");
        SaveWorkspaceButton.ToolTip = Text("Save enabled websites as a workspace");
        RunWorkspaceButton.ToolTip = Text("Run selected workspace");
        if (!_runInProgress) WorkflowStatusLabel.Text = Text("Ready");
    }

    private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingEditor) _monitorSelectionChanged = true;
    }

    private void QueueWorkflowStatus(string workflowName, AutomationEngine.WorkflowRunState state)
    {
        // The engine supplies a display name, not an identity. Never use it to select a repair.
        var cancellation = _runCancellation;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_runInProgress || !_runStatusActive || _vaultLockInProgress || _windowsLocked || _exiting ||
                cancellation is null || cancellation.IsCancellationRequested || !ReferenceEquals(_runCancellation, cancellation)) return;
            var text = state switch
            {
                AutomationEngine.WorkflowRunState.WaitingForSignIn => "Sign-in needs attention",
                AutomationEngine.WorkflowRunState.Completed => "Finished",
                AutomationEngine.WorkflowRunState.Failed => "Failed",
                AutomationEngine.WorkflowRunState.Cancelled => "Stopped",
                _ => "Running"
            };
            WorkflowStatusLabel.Text = $"{workflowName} · {LocalizationService.Text(text, _configuration.UiLanguage)}";
        }));
    }

    private static int LoginModeIndex(string mode)
    {
        return WebsiteLoginMode.Index(mode);
    }

    private static string SelectedLoginMode(int index)
    {
        return WebsiteLoginMode.FromIndex(index);
    }

    private void LoginModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectedIndex="0" in XAML fires before the AutoLoginCheck control
        // exists; startup selection is not a user edit.
        if (_loadingEditor || _configuration is null || AutoLoginCheck is null) return;
        AutoLoginCheck.IsChecked = WebsiteLoginMode.UsesSavedPassword(SelectedLoginMode(LoginModeCombo.SelectedIndex));
        MarkUnsavedChanges();
    }

    private void OpenSettingsStartup_Click(object sender, RoutedEventArgs e) => OpenSettingsCategory(2);
    private void OpenSettingsSecurity_Click(object sender, RoutedEventArgs e) => OpenSettingsCategory(3);
    private void OpenSettingsUpdates_Click(object sender, RoutedEventArgs e) => OpenSettingsCategory(4);
    private void OpenSettingsShortcuts_Click(object sender, RoutedEventArgs e) => OpenSettingsCategory(5);
    private void OpenSettingsAdvanced_Click(object sender, RoutedEventArgs e) => OpenSettingsCategory(6);
    private void OpenSettingsImportExport_Click(object sender, RoutedEventArgs e) => OpenSettingsCategory(7);

    private void OpenWebsiteAdvancedSignIn_Click(object sender, RoutedEventArgs e) => OpenSettingsCategory(6);

    private void OpenSettingsCategory(int index)
    {
        if (index >= 0 && index < SettingsTabs.Items.Count)
            SettingsTabs.SelectedIndex = index;
        MainTabs.SelectedIndex = 1;
    }

    private void UpdateRunButtons()
    {
        bool available = !_runInProgress && !_vaultLockInProgress && !_teachingInProgress && !_windowsLocked && !_exiting;
        RunAllButton.IsEnabled = available;
        RunSelectedButton.IsEnabled = available;
        var savedWorkspaceSelected = _configuration is not null &&
            WorkspaceCatalog.IsCurrentSelection(_configuration,
                WorkspaceCombo.SelectedItem as WorkspaceProfile, WorkspaceNameBox.Text);
        RunWorkspaceButton.IsEnabled = available && savedWorkspaceSelected;
        PreviewWorkspaceButton.IsEnabled = available && savedWorkspaceSelected;
        TeachButton.IsEnabled = available;
        RepairButton.IsEnabled = available;
        StopButton.IsEnabled = _runInProgress && _runCancellation is { IsCancellationRequested: false } && !_vaultLockInProgress;
        MainTabs.IsEnabled = !_vaultLockInProgress;
        RetryVaultLockButton.Visibility = _vaultCleanupFailed ? Visibility.Visible : Visibility.Collapsed;
        if (_vaultCleanupFailed)
            WorkflowStatusLabel.Text = "Browser cleanup did not complete; profiles retained and automation blocked. Retry Lock vault.";
    }

    private static System.Drawing.Icon CreateTrayIcon()
    {
        try
        {
            var stream = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/LaunchFlow.ico"))?.Stream;
            if (stream is not null) return new System.Drawing.Icon(stream);
        }
        catch { /* If a resource is unavailable, the app must still be able to start. */ }
        return System.Drawing.SystemIcons.Application;
    }

    private void ShowFromTray()
    {
        // Reopen the same window if the user double-clicks the desktop shortcut,
        // taskbar icon or notification-area icon. Do not create a second instance.
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // Windows may refuse focus to a background process; briefly putting the
        // window on top makes it visible while preserving the user's setting.
        bool wasTopmost = Topmost;
        Topmost = true;
        Topmost = wasTopmost;
        Focus();
    }

    internal void RestoreFromExternalLaunch() => ShowFromTray();

    /// <summary>Handle a .launchflow file opened from Explorer or a second-instance pipe.</summary>
    internal void QueueOpenFile(string path)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ShowFromTray();
            try
            {
                if (!File.Exists(path))
                {
                    MessageBox.Show(this, "That export file was not found:\n" + path,
                        "Open export", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (path.EndsWith(".launchflow", StringComparison.OrdinalIgnoreCase))
                    ImportLaunchFlowFile(path);
                else
                    MessageBox.Show(this, "LaunchFlow can open portable .launchflow exports from Explorer.",
                        "Open export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Open export", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }));
    }

    private bool HasPendingEditorDialog =>
        _newWorkflowWindow is not null || _openStepEditors > 0 || _teachingInProgress;

    private bool ShowTrackedStepEditor(StepEditorWindow editor)
    {
        _openStepEditors++;
        try { return editor.ShowDialog() == true; }
        finally { _openStepEditors--; }
    }

    // Both explicit Exit surfaces share this durability gate. A locked, dirty
    // vault cannot be saved: keep the window alive instead of losing edits.
    private bool TryCloseWithSavedChanges()
    {
        if (HasPendingEditorDialog)
        {
            ShowFromTray();
            MessageBox.Show(this,
                "Finish or cancel the open website editor or Teach recording before exiting.",
                "Exit paused", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (ExitSafety.TryPersistForExit(
                () => VaultDatabase.Current.IsUnlocked,
                () => _hasUnsavedChanges,
                () => SaveAll())) return true;
        ShowFromTray();
        if (_hasUnsavedChanges && !VaultDatabase.Current.IsUnlocked)
            MessageBox.Show(this,
                "You have unsaved website changes. Unlock the vault and click Save before exiting.",
                "Exit paused", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void ExitFromTray()
    {
        if (_installerShutdownInProgress || _exiting || !TryCloseWithSavedChanges()) return;
        _exiting = true;
        UpdateRunButtons();
        Close();
    }

    internal void RequestCrashCleanup()
    {
        // Dispatcher recovery can use the normal close barrier; the fatal callback cannot.
        _ = SecureVaultLockAsync("Unexpected UI error: vault locked; browser cleanup is best effort.");
    }

    internal void RequestShutdownForBuild() => _ = RequestShutdownForInstallAsync();

    internal async Task RequestShutdownForInstallAsync()
    {
        if (_installerShutdownInProgress || _exiting) return;
        _installerShutdownInProgress = true;
        _installerProfileSealFailed = false;
        try
        {
            // A modeless wizard or modal action editor can hold uncommitted data
            // that has not reached the main window's dirty flag yet.
            if (HasPendingEditorDialog)
                throw new InvalidOperationException(
                    "Finish or cancel the open website editor or Teach recording before updating.");
            // Do not allow the installer to replace running binaries until configuration,
            // browser sessions and profile ownership have been handled by the application.
            _unlockCancellation?.Cancel();
            _runCancellation?.Cancel();
            UpdateRunButtons();
            // Do not turn an installer request into consent to discard edits.
            // A clean, already-locked vault can exit; a dirty locked vault cannot.
            if (_hasUnsavedChanges && !VaultDatabase.Current.IsUnlocked)
                throw new InvalidOperationException(
                    "Unsaved changes cannot be preserved while the vault is locked. Unlock LaunchFlow and click Save before updating.");
            if (VaultDatabase.Current.IsUnlocked && !SaveAll())
                throw new InvalidOperationException("Save your unsaved changes before upgrading.");
            if (_hasUnsavedChanges)
                throw new InvalidOperationException("Unsaved changes remain. Save them before updating.");
            await SecureVaultLockAsync("Installer requested shutdown; vault locked before replacing application files.");
            // Do not collapse unrelated conditions into one message: this used
            // to tell users to Save even after a successful durable save when
            // only browser/profile cleanup failed.
            if (_hasUnsavedChanges)
                throw new InvalidOperationException(
                    "New unsaved changes appeared during shutdown. LaunchFlow remains open; unlock and save before retrying.");
            if (_vaultCleanupFailed || _vaultLockInProgress)
                throw new InvalidOperationException(
                    "A browser session could not be closed safely. LaunchFlow is still open. Close the affected browser windows, then use Lock vault and retry Setup. Profiles were retained.");
            if (_installerProfileSealFailed)
                throw new InvalidOperationException(
                    "Encrypted browser-profile sealing failed. LaunchFlow remains open and the original profile files are retained. Retry Lock vault before updating.");
            _exiting = true;
            UpdateRunButtons();
            Close();
        }
        catch (Exception ex)
        {
            ShowFromTray();
            MessageBox.Show(this, ex.Message,
                "LaunchFlow update paused", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (!_exiting)
            {
                _installerShutdownInProgress = false;
                UpdateRunButtons();
            }
        }
    }

    private static void AppendLog(string message)
    {
        // Keep developer diagnostics without an on-screen activity log.
        Debug.WriteLine(message);
    }

    private static string BrowserSelection(ComboBox combo) => BrowserChoices.SelectedName(combo);

    private void BrowserCombo_DropDownOpened(object sender, EventArgs e)
    {
        // Detect newly installed/uninstalled browsers each time the user opens
        // the selector, without destroying the current unsaved selection.
        _loadingEditor = true;
        try { BrowserChoices.Refresh(BrowserCombo); }
        finally { _loadingEditor = false; }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    // Custom caption buttons preserve the original close/save/tray prompt.
    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (TitlebarMaxButton is null) return;
        TitlebarMaxButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        TitlebarMaxButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }


    private void HideToTray_Click(object sender, RoutedEventArgs e)
    {
        if (SaveAll()) Hide();
    }

    private static string MakeBrowserFriendlyMessage(Exception ex)
    {
        var message = ex.Message ?? "The browser could not be opened.";
        if (message.Contains("existing browser session", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("profile is already in use", StringComparison.OrdinalIgnoreCase))
        {
            return "LaunchFlow could not reuse that browser profile because it is still in use by an earlier browser window. " +
                   "LaunchFlow will normally try a recovery profile automatically. If this keeps happening, close the old LaunchFlow browser window and try again.";
        }

        return message + "\n\nMake sure the selected browser is installed (Edge/Chrome) and try again.";
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitFromTray();

    private async Task<bool> ConfirmLoginOriginAsync(WebsiteWorkflow workflow, string newOrigin)
    {
        var cancellation = _runCancellation;
        bool Authorized()
        {
            var policy = EnterprisePolicy.Current;
            return cancellation is { IsCancellationRequested: false } && ReferenceEquals(_runCancellation, cancellation) &&
                !_vaultLockInProgress && !_windowsLocked && !_exiting && VaultDatabase.Current.IsUnlocked &&
                !policy.DisableAutomation && !policy.DisableCredentialFill && !policy.DisableCredentialStorage &&
                policy.IsUrlAllowed(workflow.StartUrl) && policy.IsUrlAllowed(newOrigin) &&
                (!policy.RequireHttpsForCredentials || newOrigin.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
                _configuration.Workflows.Any(w => ReferenceEquals(w, workflow));
        }
        return await Dispatcher.InvokeAsync(() =>
        {
            if (!Authorized()) return false;
            var wasHidden = !IsVisible;
            if (wasHidden) ShowFromTray();
            var result = MessageBox.Show(this,
                $"'{workflow.Name}' moved its login to:\n\n{newOrigin}\n\n" +
                "Allow LaunchFlow to enter this workflow's saved login there?\n" +
                "Only approve if you recognize your organization's real sign-in provider.",
                "Confirm SSO login website", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No);
            if (wasHidden) Hide();
            // ShowDialog pumps the dispatcher: lock, Stop and machine policy can change.
            if (result != MessageBoxResult.Yes || !Authorized()) return false;
            bool added = !workflow.ApprovedLoginOrigins.Contains(newOrigin);
            if (added) workflow.ApprovedLoginOrigins.Add(newOrigin);
            try { _store.Save(_configuration); }
            catch (Exception ex)
            {
                if (added) workflow.ApprovedLoginOrigins.Remove(newOrigin);
                AppendLog("Sign-in approval was not saved: " + ex.GetType().Name);
                return false;
            }
            AppendLog($"Approved a sign-in provider for '{workflow.Name}'.");
            return true;
        });
    }

    private TimeSpan LockTimeout => TimeSpan.FromMinutes(Math.Clamp(_configuration.VaultLockMinutes, 1, 120));

    private bool AutomationAllowed(WebsiteWorkflow? workflow = null)
    {
        var policy = EnterprisePolicy.Current;
        return !policy.DisableAutomation && (workflow is null ||
            (policy.IsUrlAllowed(workflow.StartUrl) &&
             (string.IsNullOrWhiteSpace(workflow.DestinationUrl) || policy.IsUrlAllowed(workflow.DestinationUrl))));
    }

    private bool TeachAllowed(WebsiteWorkflow workflow)
    {
        return !EnterprisePolicy.Current.DisableTeachMode && AutomationAllowed(workflow);
    }

    private bool CredentialReadAllowed()
    {
        var policy = EnterprisePolicy.Current;
        return !policy.DisableCredentialStorage && !policy.DisableCredentialFill;
    }

    private string DescribeCredentialStatus(WebsiteWorkflow w)
    {
        if (EnterprisePolicy.Current.DisableCredentialStorage)
            return "Credential access is disabled by your organization.";
        if (!VaultDatabase.Current.IsUnlocked) return "Vault locked";
        bool hasCredential;
        try { hasCredential = _vault.Has(w.Id); }
        catch (InvalidOperationException) { return "Credential access is unavailable under your organization policy."; }
        if (hasCredential)
            return string.IsNullOrWhiteSpace(w.KeepassEntryPath)
                ? "Encrypted LaunchFlow password saved"
                : "LaunchFlow's saved password is used first; remove it to use the selected KeePassXC login.";

        if (!string.IsNullOrWhiteSpace(w.KeepassEntryPath))
        {
            if (string.IsNullOrWhiteSpace(_configuration.KeePassDatabasePath))
                return $"KeePassXC login selected — choose its database in Settings → Security";
            return $"Will use KeePassXC entry “{w.KeepassEntryPath}” (prompted at run)";
        }

        return "No password saved";
    }

    private async void ChooseKeePassEntry_Click(object sender, RoutedEventArgs e)
    {
        bool Authorized()
        {
            if (!_vaultLockInProgress && !_windowsLocked && !_exiting && PasswordSessionIsValid() && CredentialReadAllowed()) return true;
            _keepassMasterPassword = null;
            CredentialStatus.Text = "Credential access is unavailable; unlock the vault and check your organization policy.";
            return false;
        }
        if (_selected is null || !Authorized()) return;
        var workflowId = _selected.Id;
        if (!KeePassXcService.IsCliAvailable())
        {
            MessageBox.Show(this, "KeePassXC could not be found. Install it and try again.",
                "KeePassXC unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var database = KeePassDbBox.Text.Trim();
        if (!File.Exists(database))
        {
            var found = KeePassXcService.FindDatabases();
            if (found.Count == 1)
            {
                SetKeePassDatabase(found[0].Path);
                database = found[0].Path;
            }
            else
            {
                SettingsTabs.SelectedIndex = 3;
                MainTabs.SelectedIndex = 1;
                MessageBox.Show(this,
                    "First choose your KeePassXC database in Settings → Security. Then open Settings → Passwords and click Choose KeePassXC login.",
                    "Connect KeePassXC", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        var secret = _keepassMasterPassword;
        if (secret is null)
        {
            var prompt = new SecretPromptWindow("Unlock KeePassXC",
                "Enter the KeePassXC master password to list your login names. Your saved passwords will not be displayed or copied into LaunchFlow.")
            { Owner = this };
            if (prompt.ShowDialog() != true) return;
            if (!Authorized()) return;
            secret = prompt.Secret ?? "";
        }

        try
        {
            if (!Authorized()) return;
            var entries = await KeePassXcService.ListEntriesAsync(
                database, KeePassKeyBox.Text.Trim(), secret);
            if (!Authorized()) return;
            if (entries.Count == 0)
            {
                MessageBox.Show(this, "No login entries were found in this KeePassXC database.",
                    "No logins found", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var picker = new KeePassEntryPickerWindow(entries) { Owner = this };
            if (picker.ShowDialog() != true || string.IsNullOrEmpty(picker.SelectedPath) || !Authorized()) return;
            var workflow = _configuration.Workflows.FirstOrDefault(w => w.Id == workflowId);
            if (workflow is null) return;
            bool hasLocalCredential = _vault.Has(workflow.Id);

            _keepassMasterPassword = secret;
            workflow.KeepassEntryPath = picker.SelectedPath;
            _configuration.KeePassDatabasePath = database;
            _configuration.KeePassKeyFilePath = KeePassKeyBox.Text.Trim();
            if (_selected?.Id == workflowId)
                KeepassEntryBox.Text = picker.SelectedPath;
            _store.Save(_configuration);
            CredentialStatus.Text = DescribeCredentialStatus(workflow);
            if (hasLocalCredential)
                MessageBox.Show(this,
                    "Login selected. This website already has a password saved in LaunchFlow, which takes priority. To use KeePassXC, click Remove saved credentials in Settings → Passwords.",
                    "KeePassXC login selected", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _keepassMasterPassword = null;
            AppendLog("KeePass selection failed: " + ex.GetType().Name);
            MessageBox.Show(this, "The KeePassXC login could not be selected. Check vault authorization, organization policy and the database connection.", "Couldn't read KeePassXC",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool EnsureKeePassPasswordForRun(IReadOnlyList<WebsiteWorkflow> workflows)
    {
        if (!CredentialReadAllowed())
        {
            _keepassMasterPassword = null;
            return true; // Engine may open/manual sign-in; never fetch a fallback secret.
        }
        var needsKeePass = workflows.Any(NeedsKeePassForRun);
        if (!needsKeePass) return true;

        if (string.IsNullOrWhiteSpace(_configuration.KeePassDatabasePath) ||
            !File.Exists(_configuration.KeePassDatabasePath))
        {
            MessageBox.Show(this,
                "One or more workflows use a KeePassXC entry, but no valid database path is set.\n\nOpen Settings → Security and choose your .kdbx file, or save the login in the LaunchFlow vault.",
                "KeePassXC not configured", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (_keepassMasterPassword is not null) return true;

        ShowFromTray();
        var prompt = new SecretPromptWindow(
            "KeePassXC password",
            "Enter the KeePassXC master password for this run. It is kept only while LaunchFlow’s vault session is unlocked and is never written to disk. Leave blank if the database uses a key file with no password.")
        {
            Owner = this
        };
        if (prompt.ShowDialog() != true) return false;
        if (_vaultLockInProgress || _windowsLocked || _exiting || !VaultDatabase.Current.IsUnlocked ||
            !CredentialReadAllowed() || workflows.Any(workflow => !AutomationAllowed(workflow)))
        {
            _keepassMasterPassword = null;
            return false;
        }
        _keepassMasterPassword = prompt.Secret ?? "";
        return true;
    }

    private bool NeedsKeePassForRun(WebsiteWorkflow w)
    {
        if (!CredentialReadAllowed() || !VaultDatabase.Current.IsUnlocked) return false;
        if (!w.AutoLogin) return false;
        if (string.Equals(w.LoginMode, "None", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(w.LoginMode, "SSO", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(w.KeepassEntryPath)) return false;
        // A policy race must abort the prompt decision, never become a vault miss.
        if (_vault.Has(w.Id)) return false;
        return true;
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        // Installer/tray explicit exits already passed the durability gate.
        // A newly dirty editor must still cancel instead of silently closing.
        if (_exiting)
        {
            if (_hasUnsavedChanges || HasPendingEditorDialog)
            {
                e.Cancel = true;
                _exiting = false;
                UpdateRunButtons();
                ShowFromTray();
            }
            return;
        }
        // Do not open modal windows once WPF has begun OS shutdown.
        // A forced Windows termination remains a separate native acceptance gate.
        if (System.Windows.Application.Current.Dispatcher.HasShutdownStarted)
        {
            if (VaultDatabase.Current.IsUnlocked) SaveAll();
            return;
        }

        var choice = new CloseChoiceWindow { Owner = this };
        choice.ShowDialog();
        if (choice.Choice == CloseChoice.Cancel)
        {
            e.Cancel = true;
            return;
        }

        // Hiding the process preserves edits in memory; explicit exit must
        // additionally pass the cold-storage durability gate.
        if (choice.Choice == CloseChoice.Background)
        {
            if (VaultDatabase.Current.IsUnlocked && !SaveAll())
            {
                e.Cancel = true;
                return;
            }
            e.Cancel = true;
            Hide();
            AppendLog("Running in the system tray. Reopen from its icon or the desktop shortcut; choose Exit in the tray menu to stop LaunchFlow.");
            return;
        }

        // Exit completely only after the same durable-save gate as tray Exit.
        if (!TryCloseWithSavedChanges())
        {
            e.Cancel = true;
            return;
        }
        _exiting = true;
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo && lParam != IntPtr.Zero)
        {
            ConstrainMaximizedWindowToWorkArea(hwnd, lParam);
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            _ = RunEnabledAsync();
        }
        return IntPtr.Zero;
    }

    private async void OnClosed(object sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) UnregisterHotKey(handle, HotkeyId);
        _windowSource?.RemoveHook(HotkeyHook);
        SystemEvents.SessionSwitch -= HandleSessionSwitch;
        _passwordSessionTimer.Stop();
        _passwordSessionTimer.Tick -= PasswordSessionTimer_Tick;
        ClearCredentialEditor();
        _runCancellation?.Cancel();
        _unlockCancellation?.Cancel();
        _lockTimeoutCancellation?.Cancel();
        _lockTimeoutCancellation?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        try
        {
            await _vaultLockTask;
            if (_activeRunTask is not null) await _activeRunTask;
            if (_activeTeachTask is not null) await _activeTeachTask;
        }
        catch { /* cancelled or failed during shutdown */ }
        try { await _runner.DisposeAsync(); } catch { /* closed during shutdown */ }
        _runCancellation?.Dispose();
        _unlockCancellation?.Dispose();
    }
}
