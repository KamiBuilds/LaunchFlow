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
        MainTabs.SelectedIndex = 1;
        if (index >= 0 && index < SettingsTabs.Items.Count)
            SettingsTabs.SelectedIndex = index;
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

    private void ShowEmptyEditor()
    {
        _loadingEditor = true;
        // An inactive editor looks like a blank workflow form. Display a true
        // empty-state view until an existing workflow is selected.
        WorkflowEditorScroll.Visibility = Visibility.Collapsed;
        WorkflowEmptyState.Visibility = Visibility.Visible;
        bool noWorkflows = _configuration.Workflows.Count == 0;
        EmptyStateHeading.Text = noWorkflows ? "No websites yet" : "Select a website";
        EmptyStateDescription.Text = noWorkflows
            ? "Start by adding one website and choosing a browser and screen. Login and recorded actions are optional."
            : "Choose one of your saved websites on the left to view its settings, or add another website.";
        CreateFirstWorkflowButton.Content = noWorkflows ? "+ Add your first website" : "+ Add another website";
        DuplicateWorkflowButton.IsEnabled = false;
        DeleteWorkflowButton.IsEnabled = false;
        NameBox.Text = "";
        KeepassEntryBox.Text = "";
        ClearCredentialEditor();
        StepList.ItemsSource = null;
        CredentialStatus.Text = "No website selected";
        PasswordSiteName.Text = "Select a website in the Websites tab";
        _loadingEditor = false;
    }

    private void ShowEditor(WebsiteWorkflow w)
    {
        _loadingEditor = true;
        WorkflowEmptyState.Visibility = Visibility.Collapsed;
        WorkflowEditorScroll.Visibility = Visibility.Visible;
        DuplicateWorkflowButton.IsEnabled = true;
        DeleteWorkflowButton.IsEnabled = true;
        EditorPanel.IsEnabled = true;
        EnabledCheck.IsChecked = w.Enabled;
        NameBox.Text = w.Name;
        StartUrlBox.Text = w.StartUrl;
        DestinationUrlBox.Text = w.DestinationUrl;
        BrowserChoices.Refresh(BrowserCombo, w.Browser);
        // Refresh on selection too: laptop docking may have changed since startup.
        var monitors = MonitorService.GetMonitors();
        MonitorCombo.ItemsSource = monitors;
        MonitorCombo.SelectedItem = MonitorService.Resolve(w, monitors);
        _monitorSelectionChanged = false;
        var resolvedMode = WebsiteLoginMode.Resolve(w.LoginMode, w.AutoLogin);
        LoginModeCombo.SelectedIndex = LoginModeIndex(resolvedMode);
        UsernameContinueBox.Text = w.UsernameContinueSelector;
        PasswordOriginBox.Text = w.PasswordCredentialOrigin;
        AutoLoginCheck.IsChecked = WebsiteLoginMode.UsesSavedPassword(resolvedMode);
        KeepassEntryBox.Text = w.KeepassEntryPath ?? "";
        UpdateKeePassEntryLabel();
        UsernameSelectorBox.Text = w.UsernameSelector;
        PasswordSelectorBox.Text = w.PasswordSelector;
        SubmitSelectorBox.Text = w.SubmitSelector;
        CredentialOriginBox.Text = w.CredentialOrigin;
        LoginSuccessSelectorBox.Text = w.LoginSuccessSelector;
        LoginSuccessUrlBox.Text = w.LoginSuccessUrlContains;
        LoginWaitBox.Text = w.LoginWaitSeconds.ToString();
        ClearCredentialEditor();
        PasswordSiteName.Text = w.Name;
        CredentialStatus.Text = DescribeCredentialStatus(w);
        if (MainTabs.SelectedIndex == 1 && SettingsTabs.SelectedIndex == 1 && PasswordSessionIsValid()) LoadCredentialEditor(w);
        StepList.ItemsSource = w.Steps;
        StepList.Items.Refresh();
        _loadingEditor = false;
    }

    private void ClearCredentialEditor()
    {
        UsernameBox.Clear();
        PasswordInput.Clear();
        _loadedCredentialWorkflowId = null;
    }

    private void LoadCredentialEditor(WebsiteWorkflow w)
    {
        ClearCredentialEditor();
        if (!CredentialReadAllowed())
        {
            CredentialStatus.Text = "Credential access is disabled by your organization.";
            return;
        }
        try
        {
            // Never put saved usernames or passwords into UI controls, even masked ones.
            // New values are entered only when the user explicitly wants to replace them.
            _loadedCredentialWorkflowId = w.Id;
            CredentialStatus.Text = DescribeCredentialStatus(w);
        }
        catch (Exception ex)
        {
            CredentialStatus.Text = "Credential cannot be decrypted";
            AppendLog("Vault decryption error: " + ex.GetType().Name);
        }
    }

    private bool PasswordSessionIsValid() =>
        _passwordsUnlocked && VaultDatabase.Current.IsUnlocked && DateTimeOffset.UtcNow < _passwordSessionExpiresUtc;

    private void PasswordSessionTimer_Tick(object? sender, EventArgs e)
    {
        if (_passwordsUnlocked && !PasswordSessionIsValid())
            LockPasswordManager("The password manager locked after one hour.");
        else if (PasswordSessionIsValid())
            PasswordSessionLabel.Text = $"Unlocked · password manager tab expires at {_passwordSessionExpiresUtc.ToLocalTime():HH:mm}. Vault relocks after {_configuration.VaultLockMinutes} minutes of Windows lock.";
    }

    private void LockPasswordManager(string reason)
    {
        if (!_passwordsUnlocked) return;
        // Locking switches tabs and clears credential controls. Those internal UI
        // transitions must not create a fresh unsaved website edit after the
        // installer has already durably saved the configuration.
        var wasLoading = _loadingEditor;
        _loadingEditor = true;
        try
        {
            if (!_installerShutdownInProgress)
            {
                try { ApplyCurrentEditor(); }
                catch (Exception ex) { AppendLog("Cannot save credential changes before lock: " + ex.GetType().Name); }
            }
            _passwordsUnlocked = false;
            _passwordSessionExpiresUtc = DateTimeOffset.MinValue;
            ClearCredentialEditor();
            PasswordSessionLabel.Text = "Locked · unlock to manage saved credentials.";
            if (MainTabs.SelectedIndex == 1 && SettingsTabs.SelectedIndex == 1)
            {
                _revertingTab = true;
                try { SettingsTabs.SelectedIndex = 0; }
                finally { _revertingTab = false; }
            }
            AppendLog(reason);
        }
        finally { _loadingEditor = wasLoading; }
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
        // The Passwords tab has an explicit Save credentials button. This avoids
        // accidentally overwriting a working login when switching workflows.
    }

    private void MarkUnsavedChanges()
    {
        if (_loadingEditor || _configuration is null || _exiting) return;
        _hasUnsavedChanges = true;
        SaveStatusLabel.Text = "Unsaved changes";
        SaveStatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(255, 202, 111));
        SaveChangesButton.FontWeight = FontWeights.Bold;
        SaveChangesButton.ToolTip = _hasUnsavedChanges ? "Website changes have not been saved" : null;
    }

    private void MarkSaved()
    {
        _hasUnsavedChanges = false;
        SaveStatusLabel.Text = "All changes saved";
        SaveStatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(133, 201, 185));
        SaveChangesButton.FontWeight = FontWeights.Normal;
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
            foreach (var w in _configuration.Workflows)
            {
                AutomationEngine.ValidateUrl(w.StartUrl);
                if (!string.IsNullOrWhiteSpace(w.DestinationUrl)) AutomationEngine.ValidateUrl(w.DestinationUrl);
                if (!string.IsNullOrWhiteSpace(w.CredentialOrigin))
                {
                    AutomationEngine.ValidateUrl(w.CredentialOrigin);
                    if (!new Uri(w.CredentialOrigin).Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException($"{w.Name}: Trusted login origin must use HTTPS.");
                }
                if (!string.IsNullOrWhiteSpace(w.PasswordCredentialOrigin))
                {
                    AutomationEngine.ValidateUrl(w.PasswordCredentialOrigin);
                    if (!new Uri(w.PasswordCredentialOrigin).Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException($"{w.Name}: Trusted password page must use HTTPS.");
                }
            }
            _store.SaveDurable(_configuration);
            MarkSaved();
            try
            {
                if (_configuration.PreferTaskSchedulerAutostart)
                {
                    TaskSchedulerService.Apply(_configuration.StartWithWindows);
                    StartupService.Apply(false); // avoid double-start via Run key
                }
                else
                {
                    TaskSchedulerService.Apply(false);
                    StartupService.Apply(_configuration.StartWithWindows);
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
            MessageBox.Show(this, ex.Message, "Cannot save LaunchFlow", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void WorkflowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Item selection can be raised during XAML construction; the editor
        // and vault-backed configuration are not yet available at that point.
        if (_loadingEditor || _configuration is null || WorkflowList is null ||
            WorkflowEmptyState is null || NameBox is null) return;
        try { ApplyCurrentEditor(); }
        catch (Exception ex)
        {
            AppendLog("Could not save previous credential: " + ex.Message);
            // Do not switch entries if the previous password could not be encrypted.
            _loadingEditor = true;
            WorkflowList.SelectedItem = _selected;
            _loadingEditor = false;
            return;
        }
        _selected = WorkflowList.SelectedItem as WebsiteWorkflow;
        if (_selected is null) ShowEmptyEditor();
        else ShowEditor(_selected);
    }

    private void RefreshWorkflowList(WebsiteWorkflow? selection)
    {
        _loadingEditor = true;
        WorkflowList.Items.Refresh();
        WorkflowList.SelectedItem = selection;
        _selected = selection;
        _loadingEditor = false;
        if (selection is null) ShowEmptyEditor();
        else ShowEditor(selection);
    }

    private static Task<bool> ShowNewWorkflowAsync(NewWorkflowWindow wizard)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        wizard.Closed += (_, _) => completion.TrySetResult(wizard.WasCreated);
        try { wizard.Show(); }
        catch (Exception ex) { completion.TrySetException(ex); }
        return completion.Task;
    }

    private async void AddWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (_vaultLockInProgress || _exiting || !RequireMasterPassword()) return;
        if (_newWorkflowWindow is not null)
        {
            _newWorkflowWindow.Activate();
            return;
        }

        try { ApplyCurrentEditor(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot save previous website", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // An unowned setup window stays open when LaunchFlow is minimized.
        var wizard = new NewWorkflowWindow();
        _newWorkflowWindow = wizard;
        try
        {
            if (!await ShowNewWorkflowAsync(wizard)) return;
            if (_vaultLockInProgress || _windowsLocked || _exiting || !VaultDatabase.Current.IsUnlocked) return;
            var next = wizard.CreatedWorkflow;
            if (next is null) return;
            // Show the next-step dialog once, only for a genuinely first
            // website. Upgraded users with existing websites are not interrupted.
            var firstWebsite = _configuration.Workflows.Count == 0 &&
                !_configuration.FirstWebsiteGuidanceShown;
            var previousShortcutTip = _configuration.ShortcutTipShown;
            var previousFirstGuidance = _configuration.FirstWebsiteGuidanceShown;
            try
            {
                // The encrypted credentials and website settings must persist
                // before showing any nonessential UI message.
                if (!string.IsNullOrEmpty(wizard.NewPassword))
                    _vault.Save(next.Id, new(wizard.NewUsername, wizard.NewPassword));
                _configuration.Workflows.Add(next);
                if (firstWebsite)
                {
                    _configuration.FirstWebsiteGuidanceShown = true;
                    _configuration.ShortcutTipShown = true; // avoid a second first-use dialog on Save
                }
                _store.SaveDurable(_configuration);
            }
            catch (Exception ex)
            {
                _configuration.Workflows.Remove(next);
                _configuration.FirstWebsiteGuidanceShown = previousFirstGuidance;
                _configuration.ShortcutTipShown = previousShortcutTip;
                try { _vault.Delete(next.Id); } catch { /* preserve recovery on partial storage error */ }
                MessageBox.Show(this, "The new website could not be saved: " + ex.Message,
                    "Create website", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            RefreshWorkflowList(next);
            AppendLog($"Created website '{next.Name}' with " +
                (next.AutoLogin ? "an encrypted login." : "no automatic login."));
            // No repeated confirmation modal; the new website is selected and
            // Test this site is immediately available.
            if (firstWebsite)
                MessageBox.Show(this,
                    WebsiteNextStepGuidance.ForCreatedWebsite(next.Name, next.AutoLogin, true),
                    WebsiteNextStepGuidance.DialogTitle(true),
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open the new website window: " + ex.Message,
                "Create workflow", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            wizard.ClearPendingSecrets();
            if (ReferenceEquals(_newWorkflowWindow, wizard)) _newWorkflowWindow = null;
        }
    }

    private void DuplicateWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        ApplyCurrentEditor();
        var next = _selected.CloneWithoutCredential();
        _configuration.Workflows.Add(next);
        RefreshWorkflowList(next);
        MarkUnsavedChanges();
        AppendLog("Workflow duplicated without a password (add credentials separately).");
    }

    private void DeleteWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var w = _selected;
        if (MessageBox.Show(this, $"Delete '{w.Name}' and its encrypted credential?", "Delete workflow",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        // Clear selection first so a pending password is not written to a deleted workflow.
        _loadingEditor = true;
        WorkflowList.SelectedItem = null;
        _configuration.Workflows.Remove(w);
        _vault.Delete(w.Id);
        _selected = null;
        _loadingEditor = false;
        RefreshWorkflowList(_configuration.Workflows.FirstOrDefault());
        _store.Save(_configuration);
        AppendLog("Workflow and encrypted credential deleted.");
    }

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

    private void RefreshSteps()
    {
        if (_selected is null) return;
        StepList.ItemsSource = null;
        StepList.ItemsSource = _selected.Steps;
    }

    private void SelectAllSteps_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) StepList.SelectAll();
    }

    private void QuickWaitStep_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        _selected.Steps.Add(new WorkflowStep
        {
            Type = "Wait", Target = "1000", Description = "Wait 1 second for the page"
        });
        RefreshSteps();
        StepList.SelectedIndex = _selected.Steps.Count - 1;
        MarkUnsavedChanges();
    }

    private bool IsCurrentStepEditorOwner(WebsiteWorkflow workflow, long revision) =>
        revision == _workspaceRevision &&
        ReferenceEquals(_selected, workflow) &&
        _configuration.Workflows.Any(item => ReferenceEquals(item, workflow)) &&
        !_windowsLocked && !_vaultLockInProgress && !_exiting &&
        VaultDatabase.Current.IsUnlocked;

    private void AddPage_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } workflow) return;
        var revision = _workspaceRevision;
        var editor = new StepEditorWindow(new WorkflowStep { Type = "Navigate", Target = "https://" }) { Owner = this };
        if (!ShowTrackedStepEditor(editor)) return;
        if (!IsCurrentStepEditorOwner(workflow, revision)) return;
        workflow.Steps.Add(editor.Result);
        RefreshSteps();
        MarkUnsavedChanges();
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

    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } workflow) return;
        var revision = _workspaceRevision;
        var editor = new StepEditorWindow { Owner = this };
        if (!ShowTrackedStepEditor(editor) || !IsCurrentStepEditorOwner(workflow, revision)) return;
        workflow.Steps.Add(editor.Result);
        RefreshSteps();
        MarkUnsavedChanges();
    }

    private void EditStep_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || StepList.SelectedItems.Count != 1 ||
            StepList.SelectedItem is not WorkflowStep current) return;
        var workflow = _selected;
        var revision = _workspaceRevision;
        int index = workflow.Steps.IndexOf(current);
        if (index < 0) return;
        var editor = new StepEditorWindow(current) { Owner = this };
        if (!ShowTrackedStepEditor(editor)) return;
        // ShowDialog pumps WPF messages. Import can replace the entire workflow
        // with an object having the same GUID and step count while it is open.
        if (!IsCurrentStepEditorOwner(workflow, revision) ||
            !WorkflowEditGuard.IsCurrent(_configuration, _selected, workflow, current,
                index, revision, _workspaceRevision) ||
            !ReferenceEquals(StepList.SelectedItem, current))
        {
            AppendLog("Step edit canceled because the website changed while its editor was open.");
            return;
        }
        workflow.Steps[index] = editor.Result;
        RefreshSteps();
        StepList.SelectedIndex = index;
        MarkUnsavedChanges();
    }

    private void RemoveStep_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || StepList.SelectedItems.Count == 0) return;
        var selected = StepList.SelectedItems.Cast<WorkflowStep>().ToArray();
        if (selected.Length > 1 && MessageBox.Show(this,
            $"Delete {selected.Length} selected actions? This takes effect when you save.",
            "Delete actions", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        foreach (var step in selected) _selected.Steps.Remove(step);
        RefreshSteps();
        MarkUnsavedChanges();
    }

    private void MoveStepUp_Click(object sender, RoutedEventArgs e) => MoveStep(-1);
    private void MoveStepDown_Click(object sender, RoutedEventArgs e) => MoveStep(1);
    private void MoveStep(int offset)
    {
        if (_selected is null || StepList.SelectedItems.Count != 1 ||
            StepList.SelectedItem is not WorkflowStep current) return;
        int index = _selected.Steps.IndexOf(current);
        int dest = index + offset;
        if (dest < 0 || dest >= _selected.Steps.Count) return;
        _selected.Steps.RemoveAt(index);
        _selected.Steps.Insert(dest, current);
        RefreshSteps(); StepList.SelectedIndex = dest;
        MarkUnsavedChanges();
    }

    private void WorkspaceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorkspaceCombo is null || WorkspaceNameBox is null) return;
        if (WorkspaceCombo.SelectedItem is WorkspaceProfile profile)
            WorkspaceNameBox.Text = profile.Name;
        if (_configuration is not null) UpdateRunButtons();
    }

    private void WorkspaceNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_configuration is null) return;
        if (WorkspaceCombo.SelectedItem is WorkspaceProfile profile &&
            !WorkspaceCatalog.IsCurrentSelection(_configuration, profile, WorkspaceNameBox.Text))
            WorkspaceCombo.SelectedItem = null;
        UpdateRunButtons();
    }

    private void SaveWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var name = WorkspaceNameBox.Text.Trim();
        if (name.Length is < 1 or > 64)
        {
            MessageBox.Show(this, "Enter a workspace name (1–64 characters).", "Workspace",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!SaveAll()) return;

        var existing = WorkspaceCatalog.FindByName(_configuration, name);
        var revision = _workspaceRevision;
        var savedWorkflowIds = _configuration.Workflows.Where(workflow => workflow.Enabled)
            .Select(workflow => workflow.Id).ToArray();
        if (existing is not null && MessageBox.Show(this,
            "Replace the workflow selection in this workspace?", "Update workspace",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        // Modal confirmations pump UI events: import may replace the saved configuration.
        if (_windowsLocked || _vaultLockInProgress || _exiting || !VaultDatabase.Current.IsUnlocked ||
            !WorkspaceCatalog.ConfirmationStillValid(
                _configuration, name, existing, revision, _workspaceRevision, savedWorkflowIds))
        {
            MessageBox.Show(this, "Workspace data changed while confirmation was open. Review the current workspaces and save again.",
                "Workspace changed", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        WorkspaceProfile profile;
        try
        {
            profile = WorkspaceCatalog.Save(_configuration, name,
                () => _store.SaveDurable(_configuration));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Workspace not saved",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _workspaceRevision++;
        WorkspaceCombo.Items.Refresh();
        WorkspaceCombo.SelectedItem = profile;
        WorkspaceNameBox.Text = profile.Name;
    }

    private void PreviewWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceCombo.SelectedItem is not WorkspaceProfile selected ||
            !WorkspaceCatalog.IsCurrentSelection(_configuration, selected, WorkspaceNameBox.Text))
        {
            MessageBox.Show(this, "Select a saved workspace first.");
            return;
        }
        new WorkspacePreviewWindow(selected, _configuration) { Owner = this }.Show();
    }

    private void RunWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceCombo.SelectedItem is not WorkspaceProfile profile ||
            !WorkspaceCatalog.IsCurrentSelection(_configuration, profile, WorkspaceNameBox.Text))
        {
            MessageBox.Show(this, "Select a saved workspace first.", "Workspace", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _ = RunEnabledAsync(workspace: profile);
    }

    private void Run_Click(object sender, RoutedEventArgs e) => _ = RunEnabledAsync();
    private void RunSelected_Click(object sender, RoutedEventArgs e) => _ = RunEnabledAsync(onlySelected: true);

    private static Task<bool> ShowRecorderAsync(VisualRecorderWindow recorder)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.Closed += (_, _) => completion.TrySetResult(recorder.Saved);
        try { recorder.Show(); }
        catch (Exception ex) { completion.TrySetException(ex); }
        return completion.Task;
    }

    private async Task SaveTaughtStepsAsync(WebsiteWorkflow workflow, IPage page)
    {
        if (_vaultLockInProgress || _windowsLocked || _exiting || !VaultDatabase.Current.IsUnlocked || !TeachAllowed(workflow)) return;
        var recorder = new VisualRecorderWindow(page) { Owner = this };
        var saved = await ShowRecorderAsync(recorder);
        // Teach uses a modeless window. Return LaunchFlow to the foreground
        // after its capture window closes, without leaving the user hunting in the taskbar.
        if (!_exiting && !_windowsLocked && IsLoaded) ShowFromTray();
        if (!saved || recorder.RecordedSteps.Count == 0) return;
        if (_vaultLockInProgress || _windowsLocked || _exiting || !VaultDatabase.Current.IsUnlocked || !TeachAllowed(workflow)) return;

        // Teach remains modeless; the selected website may have changed.
        var original = _configuration.Workflows.FirstOrDefault(w => w.Id == workflow.Id);
        if (original is null)
        {
            MessageBox.Show(this,
                "This website was removed while Teach was open, so no clicks were saved.",
                "Website no longer exists", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int previousCount = original.Steps.Count;
        original.Steps.AddRange(recorder.RecordedSteps);
        try { _store.Save(_configuration); }
        catch
        {
            original.Steps.RemoveRange(previousCount, original.Steps.Count - previousCount);
            throw;
        }
        if (_selected?.Id == original.Id) RefreshSteps();
        if (!_hasUnsavedChanges) MarkSaved();
        AppendLog($"Saved {recorder.RecordedSteps.Count} taught action(s) for '{original.Name}'.");
    }

    private async void RecordSteps_Click(object sender, RoutedEventArgs e)
    {
        if (_teachingInProgress || _runInProgress || _vaultLockInProgress || _windowsLocked || _exiting || _selected is null || !VaultDatabase.Current.IsUnlocked || !TeachAllowed(_selected) || !SaveAll()) return;
        var workflow = _selected;
        _teachingInProgress = true;
        UpdateRunButtons();
        try
        {
            _activeTeachTask = _runner.GetPageForRecorderAsync(workflow);
            var page = await _activeTeachTask;
            if (_vaultLockInProgress || _windowsLocked || _exiting || !VaultDatabase.Current.IsUnlocked || !TeachAllowed(workflow)) return;
            await SaveTaughtStepsAsync(workflow, page);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, MakeBrowserFriendlyMessage(ex),
                "Teach clicks", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _activeTeachTask = null;
            _teachingInProgress = false;
            UpdateRunButtons();
        }
    }

    private async void RepairLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_teachingInProgress || _runInProgress || _vaultLockInProgress || _windowsLocked || _exiting) return;
        try { await RepairLoginCoreAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, MakeBrowserFriendlyMessage(ex),
                "Repair login", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RepairLoginCoreAsync(WebsiteWorkflow? target = null)
    {
        var workflow = target ?? _selected;
        if (_teachingInProgress || _vaultLockInProgress || _windowsLocked || _exiting || workflow is null ||
            !VaultDatabase.Current.IsUnlocked || !TeachAllowed(workflow) || !SaveAll()) return;
        _teachingInProgress = true;
        UpdateRunButtons();
        try
        {
            // Instructions already live in the modeless Teach recorder.
            // A blocking notice here prevented users from clicking their browser
            // to sign in, which is precisely what Repair asks them to do.
            AppendLog($"Repair login for '{workflow.Name}': sign in in the browser, teach safe clicks, then Save actions.");
            WorkflowStatusLabel.Text = "Repair login · Use the browser and modeless Teach window";
            if (_vaultLockInProgress || _windowsLocked || _exiting || !VaultDatabase.Current.IsUnlocked || !TeachAllowed(workflow)) return;
            _activeTeachTask = _runner.GetPageForRecorderAsync(workflow);
            var page = await _activeTeachTask;
            if (_vaultLockInProgress || _windowsLocked || _exiting || !VaultDatabase.Current.IsUnlocked || !TeachAllowed(workflow)) return;
            var health = await LoginHealth.InspectAsync(page);
            if (health.NeedsAttention) AppendLog($"Repair login: {health.Reason}");
            await SaveTaughtStepsAsync(workflow, page);
        }
        finally
        {
            _activeTeachTask = null;
            _teachingInProgress = false;
            UpdateRunButtons();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _runCancellation?.Cancel();
        UpdateRunButtons();
    }

    private async Task RunEnabledAsync(bool onlySelected = false, WorkspaceProfile? workspace = null)
    {
        if (_runInProgress || _runner.IsRunning || _vaultLockInProgress || _windowsLocked || _teachingInProgress || _exiting || !AutomationAllowed()) return;
        // Own the UI operation before any modal prompt can pump another hotkey/tray event.
        var launchRevision = _workspaceRevision;
        _runInProgress = true;
        UpdateRunButtons();
        CancellationTokenSource? cancellation = null;
        try
        {
            if (!VaultDatabase.Current.IsUnlocked)
            {
                ShowFromTray();
                if (!RequireMasterPassword(forcePrompt: true)) return;
            }
            if (_windowsLocked || _vaultLockInProgress || _exiting) return;
            if (launchRevision != _workspaceRevision)
            {
                _keepassMasterPassword = null;
                AppendLog("Configuration was replaced while authorizing the run. Run again using the current settings.");
                return;
            }
            if (!SaveAll()) return;
            var toRun = onlySelected
                ? (_selected is null ? new List<WebsiteWorkflow>() : new List<WebsiteWorkflow> { _selected })
                : workspace is not null
                    ? workspace.Resolve(_configuration).ToList()
                    : _configuration.Workflows.Where(workflow => workflow.Enabled).ToList();
            if (toRun.Count == 0) { AppendLog("No enabled workflows to run."); return; }
            if (!WorkspaceCatalog.PendingRunIsCurrent(
                    _configuration, toRun, onlySelected, workspace, launchRevision, _workspaceRevision) ||
                (onlySelected && !ReferenceEquals(_selected, toRun[0])) ||
                (workspace is not null && (!ReferenceEquals(WorkspaceCombo.SelectedItem, workspace) ||
                    !WorkspaceCatalog.IsCurrentSelection(_configuration, workspace, WorkspaceNameBox.Text))))
            {
                _keepassMasterPassword = null;
                AppendLog("Workflow selection changed before launch. Run again using the current configuration.");
                return;
            }
            if (toRun.Any(workflow => !AutomationAllowed(workflow))) return;
            if (!EnsureKeePassPasswordForRun(toRun)) return;
            if (_windowsLocked || _vaultLockInProgress || _exiting || !VaultDatabase.Current.IsUnlocked ||
                !WorkspaceCatalog.PendingRunIsCurrent(
                    _configuration, toRun, onlySelected, workspace, launchRevision, _workspaceRevision) ||
                (onlySelected && !ReferenceEquals(_selected, toRun[0])) ||
                (workspace is not null && (!ReferenceEquals(WorkspaceCombo.SelectedItem, workspace) ||
                    !WorkspaceCatalog.IsCurrentSelection(_configuration, workspace, WorkspaceNameBox.Text))) ||
                toRun.Any(workflow => !AutomationAllowed(workflow)))
            {
                _keepassMasterPassword = null;
                return;
            }
            var monitors = MonitorService.GetMonitors();
            foreach (var workflow in toRun)
                workflow.MonitorIndex = MonitorService.Resolve(workflow, monitors).Index;
            cancellation = new CancellationTokenSource();
            _runCancellation = cancellation;
            UpdateRunButtons();
            WorkflowStatusLabel.Text = LocalizationService.Text("Running", _configuration.UiLanguage);
            _runStatusActive = true;
            _activeRunTask = _runner.RunAsync(toRun, cancellation.Token,
                includeDisabled: onlySelected, configuration: _configuration, keepassMasterPassword: _keepassMasterPassword);
            var result = await _activeRunTask;
            _runStatusActive = false;
            if (cancellation.IsCancellationRequested || _windowsLocked || _vaultLockInProgress || _exiting) return;
            var attention = result.LoginNeedsAttention;
            var steps = result.StepsNeedAttention;
            if (result.FailedCount > 0 || steps.Count > 0 || attention.Count > 0)
            {
                WorkflowStatusLabel.Text = $"{LocalizationService.Text("Failed", _configuration.UiLanguage)}: {result.FailedCount} · " +
                    $"{LocalizationService.Text("Sign-in needs attention", _configuration.UiLanguage)}: {attention.Count} · Steps needing repair: {steps.Count}";
                NotifyTray("LaunchFlow", $"{result.FailedCount} workflow(s) failed; {attention.Count} sign-in(s) need attention; {steps.Count} step repair(s). Nothing is rerun automatically.", Forms.ToolTipIcon.Warning);
                if (attention.Count > 0) await OfferRepairForLoginAttentionAsync(attention);
                if (steps.Count > 0) OfferRepairForStepAttention(steps);
            }
            else
            {
                WorkflowStatusLabel.Text = LocalizationService.Text("Finished", _configuration.UiLanguage);
                NotifyTray("LaunchFlow", toRun.Count == 1 ? $"Finished “{toRun[0].Name}”." : $"Finished {toRun.Count} workflows.");
            }
        }
        catch (OperationCanceledException)
        {
            WorkflowStatusLabel.Text = LocalizationService.Text("Stopped", _configuration.UiLanguage);
            if (!_exiting) NotifyTray("LaunchFlow", "Workflow run was stopped.");
        }
        catch (Exception ex)
        {
            WorkflowStatusLabel.Text = LocalizationService.Text("Failed", _configuration.UiLanguage);
            AppendLog("Run error: " + ex.GetType().Name);
            if (!_exiting) NotifyTray("LaunchFlow", "A workflow failed. Review the website settings and try again.", Forms.ToolTipIcon.Error);
        }
        finally
        {
            _runStatusActive = false;
            if (cancellation?.IsCancellationRequested == true)
                WorkflowStatusLabel.Text = LocalizationService.Text("Stopped", _configuration.UiLanguage);
            if (ReferenceEquals(_runCancellation, cancellation)) _runCancellation = null;
            cancellation?.Dispose();
            _activeRunTask = null;
            _runInProgress = false;
            UpdateRunButtons();
        }
    }

    private Task OfferRepairForLoginAttentionAsync(
        IReadOnlyList<AutomationEngine.LoginAttention> items)
    {
        // Informational sign-in assistance is modeless. A completed run must
        // release its UI state immediately, even if the user leaves a prompt
        // open while reviewing other websites.
        ShowFromTray();
        MainTabs.SelectedIndex = 0;
        foreach (var item in items)
        {
            if (_vaultLockInProgress || _windowsLocked || _exiting ||
                !VaultDatabase.Current.IsUnlocked || _runCancellation?.IsCancellationRequested == true)
                break;
            var workflow = _configuration.Workflows.FirstOrDefault(w => w.Id == item.WorkflowId);
            if (workflow is null) continue;
            if (_openSignInDecisions.TryGetValue(workflow.Id, out var existing))
            {
                // Do not flood the user with identical windows on repeated runs.
                existing.Activate();
                continue;
            }
            WorkflowList.SelectedItem = workflow;
            var capturedRevision = _workspaceRevision;
            var dialog = new SignInDecisionWindow(item.WorkflowName, item.Reason) { Owner = this };
            _openSignInDecisions.Add(workflow.Id, dialog);
            dialog.Closed += (_, _) =>
            {
                if (_openSignInDecisions.TryGetValue(workflow.Id, out var current) &&
                    ReferenceEquals(current, dialog))
                    _openSignInDecisions.Remove(workflow.Id);
                // Closed fires synchronously; async work continues without
                // disabling the main window or showing any modal repair dialog.
                _ = ApplyModelessSignInDecisionAsync(workflow, capturedRevision, dialog.Choice);
            };
            try { dialog.Show(); }
            catch
            {
                _openSignInDecisions.Remove(workflow.Id);
                // A failed optional notice cannot turn a finished run into
                // an unsafe retry or leak private Playwright error details.
                AppendLog("Could not display the sign-in assistance window.");
            }
        }
        return Task.CompletedTask;
    }

    private async Task ApplyModelessSignInDecisionAsync(
        WebsiteWorkflow workflow, long capturedRevision, SignInDecisionWindow.Decision choice)
    {
        if (choice == SignInDecisionWindow.Decision.Later ||
            _exiting || _windowsLocked || _vaultLockInProgress ||
            !VaultDatabase.Current.IsUnlocked ||
            capturedRevision != _workspaceRevision ||
            !_configuration.Workflows.Any(w => ReferenceEquals(w, workflow))) return;

        if (choice == SignInDecisionWindow.Decision.Confirmed)
        {
            try
            {
                if (await _runner.ConfirmSignedInAsync(workflow))
                {
                    if (_exiting || _windowsLocked || _vaultLockInProgress ||
                        !VaultDatabase.Current.IsUnlocked ||
                        capturedRevision != _workspaceRevision ||
                        !_configuration.Workflows.Any(w => ReferenceEquals(w, workflow))) return;
                    WorkflowStatusLabel.Text = "Signed-in session confirmed · Run this website again to continue actions";
                    AppendLog($"User confirmed the open signed-in session for '{workflow.Name}'. Run again to continue.");
                }
                else
                {
                    AppendLog($"Sign-in confirmation for '{workflow.Name}' was unavailable; check its browser challenge.");
                    WorkflowStatusLabel.Text = "Sign-in was not confirmed · Check the browser page";
                }
            }
            catch (Exception)
            {
                AppendLog("Sign-in confirmation could not be completed safely.");
            }
            return;
        }

        if (!TeachAllowed(workflow)) return;
        try { await RepairLoginCoreAsync(workflow); }
        catch (Exception ex)
        {
            if (!_exiting)
                AppendLog("Repair login could not start: " + ex.GetType().Name);
        }
    }

    private void OfferRepairForStepAttention(IReadOnlyList<AutomationEngine.StepAttention> items)
    {
        foreach (var item in items)
        {
            if (_vaultLockInProgress || _windowsLocked || _exiting || !VaultDatabase.Current.IsUnlocked ||
                _runCancellation?.IsCancellationRequested == true) return;
            var workflow = _configuration.Workflows.FirstOrDefault(w => w.Id == item.WorkflowId);
            if (workflow is null) continue;
            ShowFromTray();
            MainTabs.SelectedIndex = 0;
            WorkflowList.SelectedItem = workflow;
            // Step failures are informational; never disable the workspace
            // behind a modal alert after the automation has already stopped.
            AppendLog($"Action {item.StepType} needs review for '{item.WorkflowName}'. Later steps were not run.");
            // Preserve the final run's failed/sign-in/step counts in the status bar.
            NotifyTray("Workflow steps need review",
                $"Review the {item.StepType} action for '{item.WorkflowName}' using Edit or Teach.",
                Forms.ToolTipIcon.Warning);
        }
    }

    private void NotifyTray(string title, string message, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info)
    {
        if (!_configuration.TrayNotificationsEnabled) return;
        try
        {
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = message.Length > 240 ? message[..240] + "…" : message;
            _tray.BalloonTipIcon = icon;
            _tray.ShowBalloonTip(4500);
        }
        catch { /* tray may be unavailable */ }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.Show();
    }

    private void BrowserSetup_Click(object sender, RoutedEventArgs e) =>
        new BrowserSetupWindow { Owner = this }.Show();

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Export a local support report with application version and redacted event codes? No websites, credentials or workflow details will be included.",
                "Export diagnostics", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save redacted diagnostics",
            Filter = "Text report (*.txt)|*.txt",
            DefaultExt = ".txt",
            FileName = "LaunchFlow-Diagnostics.txt"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            DiagnosticReportService.Export(dialog.FileName);
            MessageBox.Show(this, "Redacted diagnostic report saved.", "Diagnostics");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not export diagnostics: " + ex.GetType().Name,
                "Diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private TimeSpan LockTimeout => TimeSpan.FromMinutes(Math.Clamp(_configuration.VaultLockMinutes, 1, 120));

    private void HandleSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
            Dispatcher.BeginInvoke(new Action(OnWindowsLocked));
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
            Dispatcher.BeginInvoke(new Action(() => _ = OnWindowsUnlockedAsync()));
    }

    private void OnWindowsLocked()
    {
        if (_windowsLocked) return; // Duplicate Windows events must not create timers.
        _windowsLocked = true;
        UpdateRunButtons();
        _lockedSinceUtc = DateTimeOffset.UtcNow;
        _unlockCancellation?.Cancel();
        _runCancellation?.Cancel(); // Don't start new steps while the device is locked.
        _lockTimeoutCancellation?.Cancel();
        _lockTimeoutCancellation?.Dispose();
        _lockTimeoutCancellation = new CancellationTokenSource();
        _ = LockAfterThresholdAsync(_lockedSinceUtc.Value, _lockTimeoutCancellation.Token);
        AppendLog($"Windows locked. Vault locks after {_configuration.VaultLockMinutes} minutes unless Windows unlocks first.");
    }

    private async Task LockAfterThresholdAsync(DateTimeOffset lockTime, CancellationToken token)
    {
        try
        {
            await Task.Delay(LockTimeout, token);
            await Dispatcher.InvokeAsync(new Func<Task>(async () =>
            {
                // Check the actual elapsed lock interval; timers may be late
                // after laptop sleep/resume and events may arrive out of order.
                if (_windowsLocked && _lockedSinceUtc == lockTime &&
                    DateTimeOffset.UtcNow - lockTime >= LockTimeout)
                    await SecureVaultLockAsync("Windows has been locked for the configured timeout: encrypted vault locked.");
            })).Task.Unwrap();
        }
        catch (OperationCanceledException) { }
    }

    private Task SecureVaultLockAsync(string message)
    {
        if (!_vaultLockTask.IsCompleted) return _vaultLockTask;
        // Publish ownership before cancellation callbacks or modal reentrancy can lock again.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _vaultLockTask = completion.Task;
        async Task CleanupAsync()
        {
            try { await SecureVaultLockCoreAsync(message); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
        _ = CleanupAsync();
        return completion.Task;
    }

    private async Task SecureVaultLockCoreAsync(string message)
    {
        _vaultLockInProgress = true;
        _vaultCleanupFailed = false;
        _keepassMasterPassword = null;
        bool cleanupComplete = false;
        var pendingRun = _activeRunTask;
        var pendingTeach = _activeTeachTask;
        try
        {
            // Revoke first. CloseSessions is the engine's cancellation/dispatch barrier;
            // awaiting a dispatched fill before closing its context can stall forever.
            VaultDatabase.Current.Lock();
            UpdateRunButtons();
            try { _runCancellation?.Cancel(); }
            catch (Exception ex) { AppendLog("Cancellation callback failed during lock: " + ex.GetType().Name); }
            LockPasswordManager(message);
            ClearCredentialEditor();
            try { await _runner.CloseSessionsAsync(); }
            catch (Exception ex)
            {
                // A browser can close at the same moment as installer shutdown,
                // leaving a transient Playwright close error. Retry the remaining
                // tracked contexts once, never kill a browser or discard profiles.
                var recovered = false;
                if (_installerShutdownInProgress)
                {
                    await Task.Delay(350);
                    try
                    {
                        await _runner.CloseSessionsAsync();
                        recovered = true;
                    }
                    catch (Exception retryError)
                    {
                        if (_configuration.DiagnosticsLogEnabled)
                            CrashLog.Write("CloseSessionsRetry", retryError);
                    }
                }
                if (!recovered)
                {
                    AppendLog("Browser cleanup did not complete; profiles retained and automation blocked. Close browser windows and retry Lock vault.");
                    NotifyTray("Vault locked", "Browser cleanup did not complete; profiles retained and automation blocked. Close browser windows and retry Lock vault.", Forms.ToolTipIcon.Warning);
                    if (_configuration.DiagnosticsLogEnabled) CrashLog.Write("CloseSessions", ex);
                    return; // Never wrap/delete a profile while its browser may still be live.
                }
            }
            try { if (pendingRun is not null) await pendingRun; }
            catch (Exception ex) { AppendLog("Automation ended during lock: " + ex.GetType().Name); }
            try { if (pendingTeach is not null) await pendingTeach; }
            catch (Exception ex) { AppendLog("Teach ended during lock: " + ex.GetType().Name); }
            cleanupComplete = true;
            if (_configuration.SealBrowserProfilesOnLock)
            {
                try { await Task.Run(ProfileVaultService.SealAll); }
                catch (Exception ex)
                {
                    if (_installerShutdownInProgress) _installerProfileSealFailed = true;
                    AppendLog("Could not seal browser profiles; source files retained: " + ex.GetType().Name);
                    if (_configuration.DiagnosticsLogEnabled) CrashLog.Write("SealProfiles", ex);
                }
            }
        }
        finally
        {
            _vaultLockInProgress = !cleanupComplete;
            _vaultCleanupFailed = !cleanupComplete;
            UpdateRunButtons();
        }
    }

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

    private void RefreshKeePassStatus()
    {
        var database = KeePassDbBox.Text.Trim();
        if (!KeePassXcService.IsCliAvailable())
        {
            KeePassStatusLabel.Text = "KeePassXC was not found. Install KeePassXC, then click Find my database.";
            return;
        }
        if (string.IsNullOrWhiteSpace(database))
        {
            KeePassStatusLabel.Text = "KeePassXC found. Choose a password database to continue.";
            return;
        }
        if (!File.Exists(database))
        {
            KeePassStatusLabel.Text = "The selected database is missing. Choose it again.";
            return;
        }
        KeePassStatusLabel.Text = $"KeePassXC found · {Path.GetFileName(database)} selected.";
    }

    private void SuggestKeePassDatabase()
    {
        if (!string.IsNullOrWhiteSpace(KeePassDbBox.Text)) return;
        var found = KeePassXcService.FindDatabases();
        if (found.Count == 1) KeePassDbBox.Text = found[0].Path;
    }

    private void FindKeePassDb_Click(object sender, RoutedEventArgs e)
    {
        var found = KeePassXcService.FindDatabases();
        if (found.Count == 0)
        {
            KeePassDbSuggestions.Visibility = Visibility.Collapsed;
            MessageBox.Show(this,
                "No KeePassXC database was found in the usual folders. Click Choose a file and select the .kdbx database you open in KeePassXC.",
                "Find KeePassXC database", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (found.Count == 1)
        {
            SetKeePassDatabase(found[0].Path);
            KeePassDbSuggestions.Visibility = Visibility.Collapsed;
            return;
        }

        KeePassDbSuggestions.ItemsSource = found;
        KeePassDbSuggestions.SelectedIndex = -1;
        KeePassDbSuggestions.Visibility = Visibility.Visible;
        KeePassStatusLabel.Text = $"Found {found.Count} databases. Choose the one you normally use.";
        KeePassDbSuggestions.Focus();
        KeePassDbSuggestions.IsDropDownOpen = true;
    }

    private void KeePassDbSuggestion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_configuration is null || KeePassDbSuggestions is null || KeePassDbBox is null) return;
        if (KeePassDbSuggestions.SelectedItem is KeePassXcService.DatabaseChoice selected)
        {
            SetKeePassDatabase(selected.Path);
            KeePassDbSuggestions.Visibility = Visibility.Collapsed;
        }
    }

    private void SetKeePassDatabase(string path)
    {
        KeePassDbBox.Text = path;
        _keepassMasterPassword = null;
        RefreshKeePassStatus();
    }

    private void UpdateKeePassEntryLabel()
    {
        var path = KeepassEntryBox.Text.Trim();
        SelectedKeePassEntryLabel.Text = string.IsNullOrWhiteSpace(path)
            ? "No KeePassXC login selected" : path;
    }

    private void KeePassEntryTextChanged(object sender, TextChangedEventArgs e)
    {
        if (SelectedKeePassEntryLabel is not null) UpdateKeePassEntryLabel();
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
                MainTabs.SelectedIndex = 1;
                SettingsTabs.SelectedIndex = 3;
                MessageBox.Show(this,
                    "First choose your KeePassXC database in Settings → Security. Then return to Passwords and click Choose KeePassXC login.",
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
                    "Login selected. This website already has a password saved in LaunchFlow, which takes priority. To use KeePassXC, click Remove saved credentials in Passwords.",
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

    private void BrowseKeePassDb_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select KeePassXC database",
            Filter = "KeePass database (*.kdbx)|*.kdbx|All files (*.*)|*.*"
        };
        if (!string.IsNullOrWhiteSpace(KeePassDbBox.Text))
        {
            try
            {
                var dir = Path.GetDirectoryName(KeePassDbBox.Text.Trim());
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    dialog.InitialDirectory = dir;
            }
            catch { /* ignore bad path */ }
        }
        if (dialog.ShowDialog(this) == true)
        {
            SetKeePassDatabase(dialog.FileName);
        }
    }

    private void BrowseKeePassKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select KeePass key file",
            Filter = "Key file (*.keyx;*.key)|*.keyx;*.key|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            KeePassKeyBox.Text = dialog.FileName;
            _keepassMasterPassword = null;
            RefreshKeePassStatus();
        }
    }

    /// <summary>
    /// Prompt once per vault session when a run needs KeePass and has no local vault password.
    /// </summary>
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

    private async Task OnWindowsUnlockedAsync()
    {
        if (!_windowsLocked) return; // Ignore duplicate unlock events.
        var interval = _lockedSinceUtc.HasValue ? DateTimeOffset.UtcNow - _lockedSinceUtc.Value : TimeSpan.MaxValue;
        _windowsLocked = false;
        _lockedSinceUtc = null;
        _lockTimeoutCancellation?.Cancel();
        _lockTimeoutCancellation?.Dispose();
        _lockTimeoutCancellation = null;
        if (interval >= LockTimeout)
            await SecureVaultLockAsync($"Windows was locked for {interval.TotalMinutes:F1} minutes; master password required.");
        else await _vaultLockTask;
        UpdateRunButtons();
        if (_windowsLocked || _exiting) return;
        if (!VaultDatabase.Current.IsUnlocked)
        {
            ShowFromTray();
            if (!RequireMasterPassword(forcePrompt: true))
            {
                AppendLog("Vault remains locked. No credential automation will run.");
                return;
            }
            AppendLog("Encrypted vault unlocked after Windows unlock.");
        }
        else AppendLog($"Windows unlocked after {interval.TotalSeconds:F0} seconds; continuing existing vault session.");
        if (_configuration.RunOnUnlock) await RunAfterUnlockAsync();
    }

    private async Task RunAfterUnlockAsync()
    {
        _unlockCancellation?.Cancel();
        _unlockCancellation?.Dispose();
        _unlockCancellation = new CancellationTokenSource();
        try
        {
            int seconds = Math.Clamp(_configuration.UnlockDelaySeconds, 0, 300);
            AppendLog($"Windows session unlocked: starting in {seconds} seconds.");
            await Task.Delay(TimeSpan.FromSeconds(seconds), _unlockCancellation.Token);
            await RunEnabledAsync();
        }
        catch (OperationCanceledException) { /* superseded by another session event */ }
    }

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

    private static bool ParseShortcut(string shortcut, out uint flags, out uint key)
    {
        flags = 0x4000; // MOD_NOREPEAT
        key = 0;
        var parts = shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        foreach (var part in parts[..^1])
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) flags |= 0x0002;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) flags |= 0x0001;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) flags |= 0x0004;
            else return false;
        }
        if ((flags & 7) == 0) return false;
        if (!Enum.TryParse<Key>(parts[^1], true, out var parsed)) return false;
        if (parsed is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System) return false;
        key = (uint)KeyInterop.VirtualKeyFromKey(parsed);
        return key != 0;
    }

    private bool TrySetShortcut(string shortcut)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !ParseShortcut(shortcut, out uint modifiers, out uint key)) return false;
        if (_activeShortcut.Length > 0) UnregisterHotKey(handle, HotkeyId);
        if (!RegisterHotKey(handle, HotkeyId, modifiers, key))
        {
            if (_activeShortcut.Length > 0 && ParseShortcut(_activeShortcut, out var oldMods, out var oldKey))
                RegisterHotKey(handle, HotkeyId, oldMods, oldKey);
            AppendLog($"Shortcut {shortcut} is reserved or already in use by another app.");
            return false;
        }
        _activeShortcut = shortcut;
        ShortcutBox.Text = shortcut;
        ShortcutHelpLabel.Text = $"{shortcut} is active even while LaunchFlow is hidden in the tray.";
        AppendLog($"Global shortcut activated: {shortcut}.");
        return true;
    }

    private void CaptureShortcut_Click(object sender, RoutedEventArgs e)
    {
        _capturingShortcut = true;
        CaptureButton.Content = "Press keys…";
        ShortcutHelpLabel.Text = "Press Ctrl, Alt or Shift together with a letter or function key. Esc cancels.";
        Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingShortcut) return;
        if (e.Key == Key.Escape) { _capturingShortcut = false; CaptureButton.Content = "Change shortcut"; e.Handled = true; return; }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var modifiers = Keyboard.Modifiers;
        var bits = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) bits.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) bits.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) bits.Add("Shift");
        if (bits.Count == 0) { ShortcutHelpLabel.Text = "A modifier (Ctrl, Alt or Shift) is required."; return; }
        bits.Add(key.ToString());
        var requested = string.Join("+", bits);
        _capturingShortcut = false;
        CaptureButton.Content = "Change shortcut";
        e.Handled = true;
        if (!TrySetShortcut(requested))
        {
            MessageBox.Show(this, $"The shortcut {requested} could not be registered. Try another.",
                "Shortcut unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _configuration.GlobalShortcut = requested;
        _store.Save(_configuration);
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        SidebarPanel.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = new GridLength(_sidebarCollapsed ? 0 : 245);
        SidebarToggleButton.Content = _sidebarCollapsed ? "❯" : "❮";
    }

    // The navigation controls use the same MainTabs content and auth flow.
    private void TopNavigation_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, WorkflowsNav)) MainTabs.SelectedIndex = 0;
        else if (ReferenceEquals(sender, SettingsNav)) MainTabs.SelectedIndex = 1;
        SyncTopNavigation();
    }

    private void SyncTopNavigation()
    {
        WorkflowsNav.IsChecked = MainTabs.SelectedIndex == 0;
        SettingsNav.IsChecked = MainTabs.SelectedIndex == 1;
    }

    // The password editor is a secured Settings category, not a separate
    // top-level workspace. Authorization is required before showing it; leaving
    // the category clears pending secret inputs but retains the one-hour session.
    private void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_configuration is null || SettingsTabs is null || MainTabs is null ||
            UsernameBox is null || PasswordInput is null || _revertingTab ||
            !ReferenceEquals(e.OriginalSource, SettingsTabs) || MainTabs.SelectedIndex != 1)
            return;
        UpdatePasswordSettingsAccess();
    }

    private void UpdatePasswordSettingsAccess()
    {
        if (SettingsTabs.SelectedIndex == 1)
        {
            if (!PasswordSessionIsValid() && !RequireMasterPassword())
            {
                _revertingTab = true;
                try { SettingsTabs.SelectedIndex = 0; }
                finally { _revertingTab = false; }
                ClearCredentialEditor();
                return;
            }
            if (_selected is not null) LoadCredentialEditor(_selected);
            PasswordSessionTimer_Tick(null, EventArgs.Empty);
        }
        else
        {
            // Never leave unsaved credential input values in inactive tabs.
            ClearCredentialEditor();
        }
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_configuration is null || MainTabs is null || SettingsTabs is null ||
            WorkflowsNav is null || SettingsNav is null ||
            UsernameBox is null || PasswordInput is null) return;
        if (!ReferenceEquals(e.OriginalSource, MainTabs)) return;
        if (_revertingTab) { SyncTopNavigation(); return; }
        if (MainTabs.SelectedIndex == 1)
            UpdatePasswordSettingsAccess();
        else
            ClearCredentialEditor();
        SyncTopNavigation();
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

        MainTabs.SelectedIndex = 1; // Settings
        try { SettingsTabs.SelectedIndex = 7; } catch { /* optional */ }
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
