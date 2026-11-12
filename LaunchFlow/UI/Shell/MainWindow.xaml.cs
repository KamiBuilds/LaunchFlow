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
using LaunchFlow.Controllers;
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
    private readonly WorkflowConfigurationService _workflowConfiguration;
    private readonly ConfigurationSaveTransaction _saveTransaction;
    private readonly StartupRegistrationService _startupRegistration;
    private readonly MasterPasswordService _master = new();
    private TutorialWindow? _tutorial;
    private bool _passwordsUnlocked;
    private DateTimeOffset _passwordSessionExpiresUtc = DateTimeOffset.MinValue;
    private Guid? _loadedCredentialWorkflowId;
    private readonly DispatcherTimer _passwordSessionTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _windowsLocked;
    private DateTimeOffset? _lockedSinceUtc;
    private CancellationTokenSource? _lockTimeoutCancellation;

    private bool _revertingTab;
    private bool _sidebarCollapsed;
    private bool _vaultLockInProgress;
    private bool _vaultCleanupFailed;
    private bool _profileSealFailed;
    private bool _installerShutdownInProgress;
    private bool _installerProfileSealFailed;
    private bool _monitorSelectionChanged;
    private long _workspaceRevision;
    private Task _vaultLockTask = Task.CompletedTask;
    private VisualRecorderWindow? _activeRecorder;
    private readonly Dictionary<Guid, SignInDecisionWindow> _openSignInDecisions = new();
    private NewWorkflowWindow? _newWorkflowWindow;
    private int _openStepEditors;
    private bool _startupWorkflowViewReady;
    private bool _capturingShortcut;
    private readonly GlobalShortcutRegistrar _shortcuts = new();

    private readonly CredentialVault _vault;
    private readonly AutomationEngine _runner;
    private Configuration _configuration;
    private readonly RunUiOperationController _runUi = new();
    private readonly TeachUiOperationController _teachUi = new();
    private readonly WorkflowEditorSession _editorSession = new();
    private bool _loadingEditor;
    private bool _exiting;
    private string? _keepassMasterPassword;
    private long _keepassChooserGeneration;
    private long _keepassCacheRevision;
    private CancellationTokenSource? _unlockCancellation;
    private readonly Forms.NotifyIcon _tray;
    private HwndSource? _windowSource;
    private const int HotkeyId = GlobalShortcutRegistrar.DefaultHotkeyId;
    private const int WmHotkey = 0x0312;
    private const int WmGetMinMaxInfo = 0x0024;

    // Thin WPF hook; native maximize math lives in WindowWorkAreaConstraint.
    private static void ConstrainMaximizedWindowToWorkArea(IntPtr hwnd, IntPtr lParam) =>
        WindowWorkAreaConstraint.Apply(hwnd, lParam);

    public MainWindow()
    {
        // Inject the persistence, URL policy and Windows registration adapters.
        // The domain services themselves never depend on WPF or machine state.
        _workflowConfiguration = new WorkflowConfigurationService(_store.SaveDurable, AutomationEngine.ValidateUrl);
        _saveTransaction = new ConfigurationSaveTransaction(_store.SaveDurable, _workflowConfiguration.Validate);
        _startupRegistration = new StartupRegistrationService(TaskSchedulerService.Apply, StartupService.Apply);
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
            MessageBox.Show("LaunchFlow cannot open its data safely. The existing files have not been overwritten.\n\n" + RunIssueNotice.SafeExceptionReason(ex),
                "LaunchFlow startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
        // The App startup gate already unlocked the encrypted database.
        _passwordsUnlocked = true;
        _passwordSessionExpiresUtc = PasswordSessionPolicy.RenewFrom(DateTimeOffset.UtcNow);
        VaultTimeoutBox.Text = _configuration.VaultLockMinutes.ToString();
        KeePassDbBox.Text = _configuration.KeePassDatabasePath ?? "";
        KeePassKeyBox.Text = _configuration.KeePassKeyFilePath ?? "";
        SuggestKeePassDatabase();
        RefreshKeePassStatus();
        _runner = new AutomationEngine(_vault);
        _runner.ApproveCredentialOriginAsync = (workflow, origin, password, token) =>
            ConfirmLoginOriginAsync(workflow, origin, password, token);
        _runner.OnWorkflowStatus += QueueWorkflowStatus;
        _runner.OnStatusMessage += QueueStatusMessage;
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
        EnabledCheck.Checked += (_, _) => WebsiteEnabledChanged();
        EnabledCheck.Unchecked += (_, _) => WebsiteEnabledChanged();
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
        System.Windows.Application.Current.SessionEnding += OnWindowsSessionEnding;
        _passwordSessionTimer.Tick += PasswordSessionTimer_Tick;
        _passwordSessionTimer.Start();
        // Auto-save uses a debounced WPF timer and never inspects password fields.
        StartAutoSave();
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
        if (!_runUi.IsRunning) WorkflowStatusLabel.Text = Text("Ready");
    }

    private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingEditor) _monitorSelectionChanged = true;
    }

    private void QueueWorkflowStatus(string workflowName, AutomationEngine.WorkflowRunState state)
    {
        // The engine supplies a display name, not an identity. Never use it to select a repair.
        var statusLease = _runUi.CaptureLeaseForCallback();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_runUi.IsStatusCurrent(statusLease) ||
                _vaultLockInProgress || _windowsLocked || _exiting) return;
            var text = state switch
            {
                AutomationEngine.WorkflowRunState.WaitingForSignIn => "Check the browser when ready",
                AutomationEngine.WorkflowRunState.Completed => "Finished",
                AutomationEngine.WorkflowRunState.Failed => "Couldn't finish",
                AutomationEngine.WorkflowRunState.Cancelled => "Stopped",
                _ => "Running"
            };
            WorkflowStatusLabel.Text = $"{workflowName} · {LocalizationService.Text(text, _configuration.UiLanguage)}";
        }));
    }

    private void QueueStatusMessage(string workflowName, string message)
    {
        var statusLease = _runUi.CaptureLeaseForCallback();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_runUi.IsStatusCurrent(statusLease) ||
                _vaultLockInProgress || _windowsLocked || _exiting || !_runUi.IsRunning) return;
            WorkflowStatusLabel.Text = $"{workflowName} · {RunIssueNotice.SafeProgress(message)}";
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
        bool available = !_runUi.IsRunning && !_vaultLockInProgress && !_teachUi.IsBusy && !_windowsLocked && !_exiting && _finalRunnerDisposeTask is null;
        RunAllButton.IsEnabled = available;
        RunSelectedButton.IsEnabled = available;
        var savedWorkspaceSelected = _configuration is not null &&
            WorkspaceCatalog.IsCurrentSelection(_configuration,
                WorkspaceCombo.SelectedItem as WorkspaceProfile, WorkspaceNameBox.Text);
        RunWorkspaceButton.IsEnabled = available && savedWorkspaceSelected;
        PreviewWorkspaceButton.IsEnabled = available && savedWorkspaceSelected;
        TeachButton.IsEnabled = available;
        RepairButton.IsEnabled = available;
        StopButton.IsEnabled = _runUi.CanStop && !_vaultLockInProgress;
        MainTabs.IsEnabled = !_vaultLockInProgress;
        RetryVaultLockButton.Visibility = _vaultCleanupFailed ? Visibility.Visible : Visibility.Collapsed;
        if (_vaultCleanupFailed)
            WorkflowStatusLabel.Text = _profileSealFailed
                ? "Browser profile sealing did not complete; profiles retained and automation blocked. Retry Lock vault."
                : "Browser cleanup did not complete; profiles retained and automation blocked. Retry Lock vault.";
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
                MessageBox.Show(this, RunIssueNotice.SafeExceptionReason(ex), "Open export", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }));
    }

    private bool HasPendingEditorDialog =>
        _newWorkflowWindow is not null || _openStepEditors > 0 || _teachUi.IsBusy;

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
                () => _editorSession.IsDirty,
                () => SaveAll())) return true;
        ShowFromTray();
        if (_editorSession.IsDirty && !VaultDatabase.Current.IsUnlocked)
            MessageBox.Show(this,
                "You have unsaved website changes. Unlock the vault and click Save before exiting.",
                "Exit paused", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void ExitFromTray()
    {
        if (_installerShutdownInProgress || _exiting || !TryCloseWithSavedChanges()) return;
        _ = CompleteOwnedExitAsync(installer: false);
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
            if (HasPendingEditorDialog)
                throw new InvalidOperationException("Finish or cancel the open editor or Teach recording before updating.");
            _unlockCancellation?.Cancel();
            _runUi.Cancellation?.Cancel();
            UpdateRunButtons();
            if (_editorSession.IsDirty && !VaultDatabase.Current.IsUnlocked)
                throw new InvalidOperationException("Unlock LaunchFlow and save your edits before updating.");
            if (_editorSession.IsDirty && !SaveAll())
                throw new InvalidOperationException("Save your unsaved edits before updating.");
            if (_editorSession.IsDirty)
                throw new InvalidOperationException("Unsaved changes remain; the update cannot replace running files.");
            await CompleteOwnedExitAsync(installer: true);
        }
        catch (Exception ex)
        {
            AppendLog("Update exit refused: " + ex.GetType().Name);
            NotifyTray("LaunchFlow update paused",
                "Finish open editors, save changes or retry browser cleanup before updating.",
                Forms.ToolTipIcon.Warning);
        }
        finally
        {
            if (!_exitDrain.Permitted)
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
        if (!_editorSession.IsDirty || SaveAll()) Hide();
    }

    private static string MakeBrowserFriendlyMessage(Exception ex)
    {
        // Never echo raw exception data from a browser driver into a modal.
        if (ex is InvalidOperationException &&
            ex.Message is "Browser cleanup is incomplete. Retry closing sessions before continuing."
                or "The previous browser session could not be closed. Retry closing sessions before continuing.")
            return "A previous browser session could not close. Close its window and retry.";
        return RunIssueNotice.SafeExceptionReason(ex) +
            "\n\nCheck Browser readiness and verify that your selected browser is installed.";
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitFromTray();

    private async Task<bool> ConfirmLoginOriginAsync(
        WebsiteWorkflow workflow, string newOrigin, bool passwordDestination = true,
        CancellationToken runToken = default)
    {
        // Captured BEFORE dispatcher scheduling. A delayed old-run callback
        // cannot borrow the lease of a run that started in the meantime.
        var requestedLease = _runUi.CaptureLeaseForCallback();
        if (requestedLease == 0 || runToken.IsCancellationRequested) return false;
        return await Dispatcher.InvokeAsync(() =>
        {
            if (!_runUi.Owns(requestedLease)) return false;
            // The consent window pumps dispatcher events. Capture the actual
            // run/editor/configuration/vault authority on the UI thread.
            var cancellation = _runUi.Cancellation;
            var lease = requestedLease;
            if (runToken.CanBeCanceled &&
                (cancellation is null || cancellation.Token != runToken)) return false;
            var editorOwner = _editorSession.CaptureOwnership();
            var config = _configuration;
            var generation = VaultDatabase.Current.SessionGeneration;
            var sourceUrl = workflow.StartUrl;
            var initialDestination = workflow.PasswordCredentialOrigin;
            var initialApprovals = workflow.ApprovedLoginOrigins.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var visibleEditorDestination = PasswordOriginBox?.Text;
            bool Authorized()
            {
                var policy = EnterprisePolicy.Current;
                return lease != 0 && _runUi.Owns(lease) &&
                    !runToken.IsCancellationRequested &&
                    cancellation is { IsCancellationRequested: false } &&
                    ReferenceEquals(_runUi.Cancellation, cancellation) &&
                    _editorSession.IsCurrent(editorOwner) &&
                    ReferenceEquals(_configuration, config) &&
                    config.Workflows.Any(w => ReferenceEquals(w, workflow)) &&
                    string.Equals(workflow.StartUrl, sourceUrl, StringComparison.Ordinal) &&
                    string.Equals(workflow.PasswordCredentialOrigin, initialDestination, StringComparison.Ordinal) &&
                    workflow.ApprovedLoginOrigins.SetEquals(initialApprovals) &&
                    !_vaultLockInProgress && !_windowsLocked && !_exiting &&
                    VaultDatabase.Current.IsUnlocked &&
                    VaultDatabase.Current.SessionGeneration == generation &&
                    !policy.DisableAutomation && !policy.DisableCredentialFill &&
                    !policy.DisableCredentialStorage && policy.IsUrlAllowed(sourceUrl) &&
                    policy.IsUrlAllowed(newOrigin) &&
                    newOrigin.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            }

            bool ShowNativeConsent()
            {
                var dialog = new PasswordOriginApprovalWindow(workflow.Name, newOrigin,
                    passwordDestination);
                if (IsVisible && WindowState != WindowState.Minimized)
                    dialog.Owner = this;
                return dialog.ShowDialog() == true;
            }

            var decision = new PasswordOriginApprovalCoordinator(_saveTransaction).Request(
                config, workflow, newOrigin, Authorized, ShowNativeConsent, generation,
                passwordDestination
                    ? PasswordOriginApprovalCoordinator.Purpose.PasswordDestination
                    : PasswordOriginApprovalCoordinator.Purpose.UsernameOrigin);
            if (!decision.ApprovedAndPublished)
            {
                if (decision.State == PasswordOriginApprovalCoordinator.Outcome.StorageRejected)
                    AppendLog("Password page approval was not saved.");
                else if (decision.State is PasswordOriginApprovalCoordinator.Outcome.CommittedButSuperseded
                    or PasswordOriginApprovalCoordinator.Outcome.CommittedButPublicationFailed)
                    AppendLog("Password page approval was committed but the editor changed; review it before rerunning.");
                return false;
            }

            // Publishing the grant does not clear unrelated dirty editor work.
            // Never replace an intervening field edit or stale A/B/A selection.
            if (passwordDestination && PasswordOriginBox is not null &&
                ReferenceEquals(_editorSession.Selection, workflow) &&
                _editorSession.IsCurrent(editorOwner) &&
                string.Equals(PasswordOriginBox.Text, visibleEditorDestination, StringComparison.Ordinal))
            {
                var loading = _loadingEditor;
                _loadingEditor = true;
                try { PasswordOriginBox.Text = newOrigin; }
                finally { _loadingEditor = loading; }
            }
            AppendLog(passwordDestination
                ? "Approved and saved the password destination."
                : "Approved and saved the username sign-in origin.");
            return true;
        });
    }

    private TimeSpan LockTimeout => SessionLockPolicy.Timeout(_configuration.VaultLockMinutes);

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

    /// <summary>
    /// Request KeePassXC authorization only for workflows that need it, then
    /// revalidate vault, policy and run admission after the modal prompt.
    /// </summary>
    private bool EnsureKeePassPasswordForRun(IReadOnlyList<WebsiteWorkflow> workflows)
    {
        if (!CredentialReadAllowed())
        {
            PublishKeePassCache(null);
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
            PublishKeePassCache(null);
            return false;
        }
        PublishKeePassCache(prompt.Secret ?? "");
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

    private bool CanPersistForWindowsExit() =>
        !HasPendingEditorDialog && ExitSafety.TryPersistForExit(
            () => VaultDatabase.Current.IsUnlocked,
            () => _editorSession.IsDirty,
            () => SaveAll());

    private void OnWindowsSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        // SessionEnding is cancelable before WPF begins dispatcher teardown.
        // The later Closing event may already be too late to save unsaved edits.
        if (CanPersistForWindowsExit()) return;
        e.Cancel = true;
        AppendLog("Windows session shutdown paused: pending edits could not be durably saved.");
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        // Explicit exits wait for an owned cleanup permit before WPF can close.
        if (_exitDrain.Permitted)
        {
            if (_editorSession.IsDirty || HasPendingEditorDialog)
            {
                e.Cancel = true;
                _exitDrain.RevokePermit();
                _exiting = false;
                IsEnabled = true;
                UpdateRunButtons();
                ShowFromTray();
            }
            return;
        }
        // SessionEnding vetoes unsaved edits before dispatcher shutdown begins.
        // This late fallback must also refuse to treat a failed save as success.
        if (System.Windows.Application.Current.Dispatcher.HasShutdownStarted)
        {
            if (!CanPersistForWindowsExit()) e.Cancel = true;
            return;
        }
        if (_exiting)
        {
            e.Cancel = true;
            return;
        }
        var choice = new CloseChoiceWindow { Owner = this };
        choice.ShowDialog();
        if (choice.Choice == CloseChoice.Cancel)
        {
            e.Cancel = true;
            return;
        }
        if (choice.Choice == CloseChoice.Background)
        {
            if (_editorSession.IsDirty && VaultDatabase.Current.IsUnlocked && !SaveAll())
            {
                e.Cancel = true;
                return;
            }
            e.Cancel = true;
            Hide();
            AppendLog("Running in the system tray. Use Exit in the tray menu to stop LaunchFlow.");
            return;
        }
        if (!TryCloseWithSavedChanges())
        {
            e.Cancel = true;
            return;
        }
        e.Cancel = true;
        Dispatcher.BeginInvoke(new Action(() => _ = CompleteOwnedExitAsync(installer: false)));
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

    private void OnClosed(object sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _shortcuts.UnregisterOnly(handle, HotkeyId);
        _windowSource?.RemoveHook(HotkeyHook);
        SystemEvents.SessionSwitch -= HandleSessionSwitch;
        System.Windows.Application.Current.SessionEnding -= OnWindowsSessionEnding;
        _passwordSessionTimer.Stop();
        _passwordSessionTimer.Tick -= PasswordSessionTimer_Tick;
        _autoSaveTimer.Stop();
        _runUi.Cancellation?.Cancel();
        _unlockCancellation?.Cancel();
        _lockTimeoutCancellation?.Cancel();
        _lockTimeoutCancellation?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        if (_exitDrain.Permitted)
        {
            _runUi.Cancellation?.Dispose();
            _unlockCancellation?.Dispose();
        }
        else
        {
            // An unexpected/OS close has no orderly permit; cleanup is best effort.
            try { VaultDatabase.Current.Lock(); } catch { }
            _ = DisposeAfterUnexpectedCloseAsync();
        }
    }
}
