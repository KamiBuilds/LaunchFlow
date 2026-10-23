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
using Forms = System.Windows.Forms;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace LaunchFlow;

public partial class MainWindow : Window
{
    private readonly ConfigStore _store = new();
    private readonly MasterPasswordService _master = new();
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
    private bool _capturingShortcut;
    private string _activeShortcut = "";

    private readonly CredentialVault _vault;
    private readonly AutomationEngine _runner;
    private readonly Configuration _configuration;
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
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public MainWindow()
    {
        InitializeComponent();
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
        RefreshKeePassStatus();
        RefreshVaultBackupStatus();
        var browsers = BrowserEnvironment.Probe();
        BrowserReadyLabel.Text = browsers.Summary;
        _runner = new AutomationEngine(_vault);
        _runner.ApproveLoginOriginAsync = ConfirmLoginOriginAsync;
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
        DiagnosticsCheck.IsChecked = _configuration.DiagnosticsLogEnabled;
        TaskSchedulerCheck.IsChecked = _configuration.PreferTaskSchedulerAutostart;
        BackupReminderBox.Text = _configuration.BackupReminderDays.ToString();
        UpdateFeedBox.Text = _configuration.UpdateFeedUrl ?? "";
        RefreshSecurityClarity();
        MonitorCombo.ItemsSource = MonitorService.GetMonitors();
        MonitorCombo.SelectedIndex = 0;
        WorkflowList.ItemsSource = _configuration.Workflows;

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
                    MessageBox.Show(this, "That backup file was not found:\n" + path,
                        "Open backup", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (path.EndsWith(".launchflow", StringComparison.OrdinalIgnoreCase))
                    ImportLaunchFlowFile(path);
                else
                    MessageBox.Show(this, "LaunchFlow can open portable .launchflow backups from Explorer.",
                        "Open backup", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Open backup", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }));
    }

    private void ExitFromTray()
    {
        _exiting = true;
        Close();
    }

    private void AppendLog(string message)
    {
        LogBox.AppendText(message + Environment.NewLine);
        if (LogBox.LineCount > 250)
        {
            var lines = LogBox.Text.Split(Environment.NewLine);
            LogBox.Text = string.Join(Environment.NewLine, lines.Skip(lines.Length - 175));
        }
        LogBox.ScrollToEnd();
    }

    private static string BrowserSelection(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Edge";

    private void ShowEmptyEditor()
    {
        _loadingEditor = true;
        // An inactive editor looks like a blank workflow form. Display a true
        // empty-state view until an existing workflow is selected.
        WorkflowEditorScroll.Visibility = Visibility.Collapsed;
        WorkflowEmptyState.Visibility = Visibility.Visible;
        bool noWorkflows = _configuration.Workflows.Count == 0;
        EmptyStateHeading.Text = noWorkflows ? "No workflows yet" : "Select a workflow";
        EmptyStateDescription.Text = noWorkflows
            ? "Create your first workflow to choose a website, add your login, and pick a monitor. The editor will appear after you create it."
            : "Choose a workflow from the list to view and edit its settings, or create another one.";
        CreateFirstWorkflowButton.Content = noWorkflows ? "+ Create your first workflow" : "+ New workflow";
        DuplicateWorkflowButton.IsEnabled = false;
        DeleteWorkflowButton.IsEnabled = false;
        NameBox.Text = "";
        ClearCredentialEditor();
        StepList.ItemsSource = null;
        CredentialStatus.Text = "No workflow selected";
        PasswordSiteName.Text = "Select a workflow in the Workflows tab";
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
        int index = Enumerable.Range(0, BrowserCombo.Items.Count)
            .FirstOrDefault(i => ((ComboBoxItem)BrowserCombo.Items[i]).Content?.ToString() == w.Browser);
        BrowserCombo.SelectedIndex = index;
        MonitorCombo.SelectedIndex = Math.Clamp(w.MonitorIndex, 0, Math.Max(0, MonitorCombo.Items.Count - 1));
        LoginModeCombo.SelectedIndex = w.LoginMode == "None" ? 1 : 0;
        UsernameContinueBox.Text = w.UsernameContinueSelector;
        PasswordOriginBox.Text = w.PasswordCredentialOrigin;
        AutoLoginCheck.IsChecked = w.AutoLogin;
        KeepassEntryBox.Text = w.KeepassEntryPath ?? "";
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
        if (MainTabs.SelectedIndex == 1 && PasswordSessionIsValid()) LoadCredentialEditor(w);
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
        // Safely discard sensitive UI state when the session expires or Windows locks.
        // Edits must be explicitly saved before expiration if authorization has elapsed.
        try { ApplyCurrentEditor(); }
        catch (Exception ex) { AppendLog("Cannot save credential changes before lock: " + ex.GetType().Name); }
        _passwordsUnlocked = false;
        _passwordSessionExpiresUtc = DateTimeOffset.MinValue;
        ClearCredentialEditor();
        PasswordSessionLabel.Text = "Locked · unlock to manage saved credentials.";
        if (MainTabs.SelectedIndex == 1)
        {
            _revertingTab = true;
            MainTabs.SelectedIndex = 0;
            _revertingTab = false;
        }
        AppendLog(reason);
    }

    private void ApplyCurrentEditor()
    {
        if (_selected is null || _loadingEditor) return;
        var w = _selected;
        w.Enabled = EnabledCheck.IsChecked == true;
        w.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Unnamed workflow" : NameBox.Text.Trim();
        w.StartUrl = StartUrlBox.Text.Trim();
        w.DestinationUrl = DestinationUrlBox.Text.Trim();
        w.Browser = BrowserSelection(BrowserCombo);
        w.MonitorIndex = (MonitorCombo.SelectedItem as MonitorChoice)?.Index ?? 0;
        w.LoginMode = BrowserSelection(LoginModeCombo);
        w.UsernameContinueSelector = UsernameContinueBox.Text.Trim();
        w.PasswordCredentialOrigin = PasswordOriginBox.Text.Trim();
        w.AutoLogin = AutoLoginCheck.IsChecked == true;
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

    private bool SaveAll(bool showNotification = false)
    {
        try
        {
            ApplyCurrentEditor();
            _configuration.StartWithWindows = StartupCheck.IsChecked == true;
            _configuration.RunOnUnlock = UnlockCheck.IsChecked == true;
            _configuration.RunOnAppStart = LaunchCheck.IsChecked == true;
            _configuration.MinimizeToTray = TrayCheck.IsChecked == true;
            _configuration.TrayNotificationsEnabled = NotifyCheck.IsChecked == true;
            _configuration.SealBrowserProfilesOnLock = SealProfilesCheck.IsChecked == true;
            _configuration.DiagnosticsLogEnabled = DiagnosticsCheck.IsChecked == true;
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
            if (!int.TryParse(BackupReminderBox.Text, out int backupDays) || backupDays < 0 || backupDays > 365)
                throw new ArgumentException("Backup reminder days must be between 0 and 365 (0 disables).");
            _configuration.BackupReminderDays = backupDays;
            _configuration.GlobalShortcut = ShortcutBox.Text;
            _configuration.KeePassDatabasePath = KeePassDbBox.Text.Trim();
            _configuration.KeePassKeyFilePath = KeePassKeyBox.Text.Trim();
            RefreshKeePassStatus();
            RefreshVaultBackupStatus();
            RefreshSecurityClarity();
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
            _store.Save(_configuration);
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
            TryDailyVaultBackup();
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
        if (_loadingEditor) return;
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

    private void AddWorkflow_Click(object sender, RoutedEventArgs e)
    {
        try { ApplyCurrentEditor(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot save previous workflow", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var wizard = new NewWorkflowWindow { Owner = this };
        if (wizard.ShowDialog() != true) return;
        var next = wizard.CreatedWorkflow;
        if (next is null) return;
        try
        {
            // Store this workflow's credentials under its unique identifier, not by URL.
            // Two websites using the same email can have different passwords.
            if (!string.IsNullOrEmpty(wizard.NewPassword))
                _vault.Save(next.Id, new(wizard.NewUsername, wizard.NewPassword));
            wizard.ClearPendingSecrets();
            _configuration.Workflows.Add(next);
            _store.Save(_configuration);
            RefreshWorkflowList(next);
            AppendLog($"Created workflow '{next.Name}' with " +
                (next.AutoLogin ? "an encrypted login." : "manual sign-in."));
            if (!_configuration.ShortcutTipShown)
            {
                _configuration.ShortcutTipShown = true;
                _store.Save(_configuration);
                MessageBox.Show(this,
                    $"Your first workflow is ready. Press {ShortcutBox.Text} to run all enabled workflows.\n\nYou can change the shortcut under Settings → Shortcuts.",
                    "Run your workspace", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            wizard.ClearPendingSecrets();
            _configuration.Workflows.Remove(next);
            try { _vault.Delete(next.Id); } catch { /* Don't hide the original error. */ }
            MessageBox.Show(this, "The new workflow could not be saved: " + ex.Message,
                "Create workflow", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DuplicateWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        ApplyCurrentEditor();
        var next = _selected.CloneWithoutCredential();
        _configuration.Workflows.Add(next);
        RefreshWorkflowList(next);
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
        if (_selected is null) return;
        if (MessageBox.Show(this, "Remove this workflow's saved encrypted login password?", "Remove credential",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _vault.Delete(_selected.Id);
        PasswordInput.Clear();
        UsernameBox.Clear();
        CredentialStatus.Text = "No password saved";
        AppendLog("Encrypted credential removed from LaunchFlow vault.");
    }

    private void SaveCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            MessageBox.Show(this, "Select a workflow before saving credentials.",
                "No workflow selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!PasswordSessionIsValid() || _loadedCredentialWorkflowId != _selected.Id)
        {
            MessageBox.Show(this, "Unlock the Password Manager before saving credentials.",
                "Vault locked", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            _store.Save(_configuration);
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
                "Check the Activity log for the error type.",
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

    private void AddPage_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var editor = new StepEditorWindow(new WorkflowStep { Type = "Navigate", Target = "https://" }) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _selected.Steps.Add(editor.Result);
            RefreshSteps();
            SaveAll();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

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
        // Display a one-time, visible consent request; don't harvest credentials from arbitrary redirects.
        return await Dispatcher.InvokeAsync(() =>
        {
            // A first-run SSO permission question must be visible even when running in tray.
            var wasHidden = !IsVisible;
            if (wasHidden) ShowFromTray();
            var result = MessageBox.Show(this,
                $"'{workflow.Name}' moved its login to:\n\n{newOrigin}\n\n" +
                "Allow LaunchFlow to enter this workflow's saved login there?\n" +
                "Only approve if you recognize your organization's real sign-in provider.",
                "Confirm SSO login website", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No);
            if (wasHidden) Hide();
            if (result != MessageBoxResult.Yes) return false;
            workflow.ApprovedLoginOrigins.Add(newOrigin);
            _store.Save(_configuration);
            AppendLog($"Approved a sign-in provider for '{workflow.Name}'.");
            return true;
        });
    }

    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var editor = new StepEditorWindow { Owner = this };
        if (editor.ShowDialog() == true) { _selected.Steps.Add(editor.Result); RefreshSteps(); }
    }

    private void EditStep_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || StepList.SelectedItem is not WorkflowStep current) return;
        int index = _selected.Steps.IndexOf(current);
        var editor = new StepEditorWindow(current) { Owner = this };
        if (editor.ShowDialog() == true) { _selected.Steps[index] = editor.Result; RefreshSteps(); StepList.SelectedIndex = index; }
    }

    private void RemoveStep_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || StepList.SelectedItem is not WorkflowStep current) return;
        _selected.Steps.Remove(current); RefreshSteps();
    }

    private void MoveStepUp_Click(object sender, RoutedEventArgs e) => MoveStep(-1);
    private void MoveStepDown_Click(object sender, RoutedEventArgs e) => MoveStep(1);
    private void MoveStep(int offset)
    {
        if (_selected is null || StepList.SelectedItem is not WorkflowStep current) return;
        int index = _selected.Steps.IndexOf(current);
        int dest = index + offset;
        if (dest < 0 || dest >= _selected.Steps.Count) return;
        _selected.Steps.RemoveAt(index);
        _selected.Steps.Insert(dest, current);
        RefreshSteps(); StepList.SelectedIndex = dest;
    }

    private void Run_Click(object sender, RoutedEventArgs e) => _ = RunEnabledAsync();
    private void RunSelected_Click(object sender, RoutedEventArgs e) => _ = RunEnabledAsync(onlySelected: true);

    private async void RecordSteps_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || !SaveAll()) return;
        try
        {
            var page = await _runner.GetPageForRecorderAsync(_selected);
            var recorder = new VisualRecorderWindow(page) { Owner = this };
            if (recorder.ShowDialog() == true)
            {
                _selected.Steps.AddRange(recorder.RecordedSteps);
                RefreshSteps();
                _store.Save(_configuration);
                AppendLog($"Saved {recorder.RecordedSteps.Count} taught steps for {_selected.Name}.");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, MakeBrowserFriendlyMessage(ex),
                "Visual recorder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RepairLogin_Click(object sender, RoutedEventArgs e)
    {
        try { await RepairLoginCoreAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, MakeBrowserFriendlyMessage(ex), "Repair login", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RepairLoginCoreAsync()
    {
        if (_selected is null || !SaveAll()) return;
        MessageBox.Show(this,
            "Repair login opens the site so you can finish MFA/SSO or teach the missing clicks.\n\n" +
            "1. Complete any prompts in the browser.\n" +
            "2. Click Start click capture and click through the broken part.\n" +
            "3. Save steps — they run after auto-login next time.\n\n" +
            "Tip: turn off Auto-login temporarily if the site only works with a fully taught path.",
            "Repair login", MessageBoxButton.OK, MessageBoxImage.Information);
        var page = await _runner.GetPageForRecorderAsync(_selected);
        var health = await LoginHealth.InspectAsync(page);
        if (health.NeedsAttention)
            AppendLog($"Repair login: {health.Reason}");
        var recorder = new VisualRecorderWindow(page) { Owner = this };
        if (recorder.ShowDialog() == true && recorder.RecordedSteps.Count > 0)
        {
            _selected.Steps.AddRange(recorder.RecordedSteps);
            RefreshSteps();
            _store.Save(_configuration);
            AppendLog($"Repair login saved {recorder.RecordedSteps.Count} step(s) for {_selected.Name}.");
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _runCancellation?.Cancel();

    private async Task RunEnabledAsync(bool onlySelected = false)
    {
        if (_windowsLocked) { AppendLog("Windows is locked; automation was paused."); return; }
        if (!VaultDatabase.Current.IsUnlocked)
        {
            ShowFromTray();
            if (!RequireMasterPassword(forcePrompt: true))
            {
                AppendLog("Master password required; workflow was not started.");
                return;
            }
        }
        if (_runner.IsRunning) { AppendLog("Already running; ignoring duplicate trigger."); return; }
        if (!SaveAll()) return;
        var toRun = onlySelected && _selected is not null
            ? new List<WebsiteWorkflow> { _selected }
            : _configuration.Workflows.Where(w => w.Enabled).ToList();
        if (toRun.Count == 0) { AppendLog("No enabled workflows to run."); return; }
        if (!EnsureKeePassPasswordForRun(toRun))
        {
            AppendLog("KeePassXC password required; workflow was not started.");
            return;
        }
        _runCancellation?.Dispose();
        _runCancellation = new CancellationTokenSource();
        StatusLabel.Text = "Running";
        StopButton.IsEnabled = true;
        var failed = false;
        AutomationEngine.RunResult? runResult = null;
        try
        {
            runResult = await _runner.RunAsync(
                toRun,
                _runCancellation.Token,
                includeDisabled: onlySelected,
                configuration: _configuration,
                keepassMasterPassword: _keepassMasterPassword);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Run stopped.");
            NotifyTray("LaunchFlow", "Workflow run was stopped.");
        }
        catch (Exception ex)
        {
            failed = true;
            AppendLog("Run error: " + ex.Message);
            NotifyTray("LaunchFlow", "A workflow failed: " + ex.Message, Forms.ToolTipIcon.Error);
        }
        finally
        {
            StatusLabel.Text = "Ready";
            StopButton.IsEnabled = false;
            if (!failed && _runCancellation is { IsCancellationRequested: false })
            {
                if (runResult?.LoginNeedsAttention is { Count: > 0 } attention)
                {
                    var names = string.Join(", ", attention.Select(a => a.WorkflowName));
                    NotifyTray("Sign-in needs attention",
                        attention.Count == 1
                            ? $"“{attention[0].WorkflowName}” may still need MFA/SSO or Repair login."
                            : $"{attention.Count} sites need sign-in attention: {names}",
                        Forms.ToolTipIcon.Warning);
                    await OfferRepairForLoginAttentionAsync(attention);
                }
                else
                {
                    NotifyTray("LaunchFlow",
                        toRun.Count == 1
                            ? $"Finished “{toRun[0].Name}”."
                            : $"Finished {toRun.Count} workflows.");
                }
            }
        }
    }

    private async Task OfferRepairForLoginAttentionAsync(
        IReadOnlyList<AutomationEngine.LoginAttention> items)
    {
        ShowFromTray();
        MainTabs.SelectedIndex = 0;
        foreach (var item in items)
        {
            var workflow = _configuration.Workflows.FirstOrDefault(w => w.Id == item.WorkflowId);
            if (workflow is null) continue;
            WorkflowList.SelectedItem = workflow;
            var answer = MessageBox.Show(this,
                $"“{item.WorkflowName}” looks like sign-in did not finish.\n\n{item.Reason}\n\n" +
                "Further steps for this site were paused.\n\nOpen Repair login now?",
                "Sign-in needs attention",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) continue;
            try
            {
                await RepairLoginCoreAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, MakeBrowserFriendlyMessage(ex), "Repair login", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
        new AboutWindow { Owner = this }.ShowDialog();
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
            await Dispatcher.InvokeAsync(() =>
            {
                // Check the actual elapsed lock interval; timers may be late
                // after laptop sleep/resume and events may arrive out of order.
                if (_windowsLocked && _lockedSinceUtc == lockTime &&
                    DateTimeOffset.UtcNow - lockTime >= LockTimeout)
                    SecureVaultLock("Windows has been locked for the configured timeout: encrypted vault locked.");
            });
        }
        catch (OperationCanceledException) { }
    }

    private void SecureVaultLock(string message)
    {
        _runCancellation?.Cancel();
        _keepassMasterPassword = null;
        try
        {
            _runner.CloseSessionsAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppendLog("Could not close browser sessions before lock: " + ex.Message);
            if (_configuration.DiagnosticsLogEnabled) CrashLog.Write("CloseSessions", ex);
        }

        if (_configuration.SealBrowserProfilesOnLock)
        {
            try
            {
                ProfileVaultService.SealAll();
                AppendLog("Browser profiles sealed (encrypted at rest) while the vault is locked.");
            }
            catch (Exception ex)
            {
                AppendLog("Could not seal browser profiles: " + ex.Message);
                if (_configuration.DiagnosticsLogEnabled) CrashLog.Write("SealProfiles", ex);
            }
        }

        if (VaultDatabase.Current.IsUnlocked)
        {
            LockPasswordManager(message);
            VaultDatabase.Current.Lock(); // Zero the derived key, don't merely hide a tab.
            ClearCredentialEditor();
        }
    }

    private string DescribeCredentialStatus(WebsiteWorkflow w)
    {
        if (_vault.Has(w.Id))
            return "Encrypted password saved";

        if (!string.IsNullOrWhiteSpace(w.KeepassEntryPath))
        {
            if (string.IsNullOrWhiteSpace(_configuration.KeePassDatabasePath))
                return $"KeePass entry “{w.KeepassEntryPath}” — set the database path in Security";
            return $"Will use KeePassXC entry “{w.KeepassEntryPath}” (prompted at run)";
        }

        return "No password saved";
    }

    private void RefreshKeePassStatus()
    {
        var db = KeePassDbBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(db))
        {
            KeePassStatusLabel.Text = "KeePassXC: not configured";
            return;
        }

        if (!File.Exists(db))
        {
            KeePassStatusLabel.Text = "KeePassXC: database path not found";
            return;
        }

        var cli = KeePassXcService.IsCliAvailable()
            ? "keepassxc-cli found"
            : "keepassxc-cli not found (install KeePassXC)";
        var key = string.IsNullOrWhiteSpace(KeePassKeyBox.Text)
            ? "no key file"
            : "key file set";
        KeePassStatusLabel.Text = $"KeePassXC: ready · {cli} · {key}";
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
            KeePassDbBox.Text = dialog.FileName;
            RefreshKeePassStatus();
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
            RefreshKeePassStatus();
        }
    }

    /// <summary>
    /// Prompt once per vault session when a run needs KeePass and has no local vault password.
    /// </summary>
    private bool EnsureKeePassPasswordForRun(IReadOnlyList<WebsiteWorkflow> workflows)
    {
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
        _keepassMasterPassword = prompt.Secret ?? "";
        return true;
    }

    private bool NeedsKeePassForRun(WebsiteWorkflow w)
    {
        if (!w.AutoLogin) return false;
        if (string.Equals(w.LoginMode, "None", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(w.KeepassEntryPath)) return false;
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
            SecureVaultLock($"Windows was locked for {interval.TotalMinutes:F1} minutes; master password required.");
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Vault creation, migration and unlock happen in App.OnStartup,
        // before this window exists and before any startup automation.
        DarkWindowChrome.Apply(this);
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(HotkeyHook);
        TrySetShortcut(string.IsNullOrWhiteSpace(_configuration.GlobalShortcut) ? "Ctrl+M" : _configuration.GlobalShortcut);
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
        AppendLog(BrowserEnvironment.Probe().Summary);
        // A second launch might have arrived during the first-run setup wizard.
        ((App)System.Windows.Application.Current).NotifyMainWindowReady(this);
        Dispatcher.BeginInvoke(new Action(MaybeShowWelcomeChecklist));
        Dispatcher.BeginInvoke(new Action(MaybeShowBackupReminder));
        Dispatcher.BeginInvoke(new Action(() => _ = MaybeCheckUpdatesAsync()));
    }

    private void MaybeShowWelcomeChecklist()
    {
        if (_configuration.WelcomeChecklistShown) return;
        if (Environment.GetCommandLineArgs().Contains("--tray", StringComparer.OrdinalIgnoreCase))
            return;

        _configuration.WelcomeChecklistShown = true;
        try { _store.Save(_configuration); } catch { /* ignore */ }

        var browsers = BrowserEnvironment.Probe();
        MessageBox.Show(this,
            "Welcome to LaunchFlow.\n\n" +
            "1. Create a workflow for each website you want to open.\n" +
            "2. Save the login in Passwords (or point to KeePassXC).\n" +
            "3. Press your shortcut (default Ctrl+M) or click Run all.\n\n" +
            browsers.Summary + "\n\n" +
            "Your vault stays on this PC in an encrypted local database. " +
            "Use Settings → Security to back it up anytime.",
            "Get started", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RefreshVaultBackupStatus()
    {
        var backups = VaultBackupService.ListBackups();
        VaultBackupStatus.Text = backups.Count == 0
            ? $"No local backups yet. Database: {VaultDatabase.Current.DatabasePath}"
            : $"Latest backup: {Path.GetFileName(backups[0])} · keeping up to {VaultBackupService.KeepCount} copies in {VaultBackupService.BackupDirectory}";
    }

    private void TryDailyVaultBackup()
    {
        try
        {
            var today = DateTime.Now.ToString("yyyyMMdd");
            var already = VaultBackupService.ListBackups()
                .Any(path => Path.GetFileName(path).Contains(today, StringComparison.Ordinal));
            if (already) return;
            VaultBackupService.CreateBackup(VaultDatabase.Current.DatabasePath);
            _configuration.LastBackupUtc = DateTimeOffset.UtcNow.ToString("o");
            try { _store.Save(_configuration); } catch { /* ignore */ }
            RefreshVaultBackupStatus();
        }
        catch (Exception ex)
        {
            AppendLog("Daily encrypted backup skipped: " + ex.Message);
        }
    }

    private void BackupVault_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (VaultDatabase.Current.IsUnlocked) SaveAll();
            var path = VaultBackupService.CreateBackup(VaultDatabase.Current.DatabasePath);
            _configuration.LastBackupUtc = DateTimeOffset.UtcNow.ToString("o");
            _store.Save(_configuration);
            RefreshVaultBackupStatus();
            AppendLog("Encrypted vault backup created: " + path);
            MessageBox.Show(this,
                "Encrypted database copy saved:\n\n" + path +
                "\n\nThis file still requires your master password. Store portable exports separately if you need recovery on another PC.",
                "Backup complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Backup failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RestoreVault_Click(object sender, RoutedEventArgs e)
    {
        if (_runner.IsRunning)
        {
            MessageBox.Show(this, "Stop active workflows before restoring a backup.",
                "Restore backup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Restore encrypted vault database",
            Filter = "LaunchFlow vault backup (launchflow-*.db)|launchflow-*.db|SQLite database (*.db)|*.db",
            InitialDirectory = Directory.Exists(VaultBackupService.BackupDirectory)
                ? VaultBackupService.BackupDirectory
                : VaultBackupService.DataDirectory
        };
        if (dialog.ShowDialog(this) != true) return;

        var confirm = MessageBox.Show(this,
            "Replace the current encrypted vault with this backup?\n\n" +
            dialog.FileName + "\n\n" +
            "A safety copy of today’s live database is kept in the backups folder first. " +
            "You will need the master password for the backup you restore.\n\nContinue?",
            "Restore vault backup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            if (VaultDatabase.Current.IsUnlocked)
            {
                SaveAll();
                try { await _runner.CloseSessionsAsync(); } catch { /* best effort */ }
                try { ProfileVaultService.SealAll(); } catch { /* best effort */ }
                VaultDatabase.Current.Lock();
            }

            var safety = VaultBackupService.RestoreEncryptedDatabase(
                dialog.FileName, VaultDatabase.Current.DatabasePath);

            MessageBox.Show(this,
                "Backup file copied into place.\n\n" +
                (safety is null ? "" : "Previous live vault saved as:\n" + safety + "\n\n") +
                "Enter the master password for the restored vault.",
                "Vault restored — unlock required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            if (!RequireMasterPassword(forcePrompt: true))
            {
                MessageBox.Show(this,
                    "The vault is locked. LaunchFlow will close so you can reopen and unlock, or restore a different backup.",
                    "Vault locked", MessageBoxButton.OK, MessageBoxImage.Warning);
                _exiting = true;
                Close();
                return;
            }

            try { ProfileVaultService.UnsealAll(); } catch (Exception ex) { CrashLog.Write("ProfileUnseal", ex); }
            ReloadConfigurationFromVault();
            RefreshVaultBackupStatus();
            AppendLog("Restored encrypted vault from " + dialog.FileName);
            MessageBox.Show(this, "Vault restored and unlocked.", "Restore complete");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Restore failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ReloadConfigurationFromVault()
    {
        var loaded = _store.Load();
        _configuration.Workflows.Clear();
        _configuration.Workflows.AddRange(loaded.Workflows);
        _configuration.StartWithWindows = loaded.StartWithWindows;
        _configuration.RunOnUnlock = loaded.RunOnUnlock;
        _configuration.RunOnAppStart = loaded.RunOnAppStart;
        _configuration.MinimizeToTray = loaded.MinimizeToTray;
        _configuration.TrayNotificationsEnabled = loaded.TrayNotificationsEnabled;
        _configuration.UnlockDelaySeconds = loaded.UnlockDelaySeconds;
        _configuration.GlobalShortcut = loaded.GlobalShortcut;
        _configuration.VaultLockMinutes = Math.Clamp(loaded.VaultLockMinutes, 1, 120);
        _configuration.KeePassDatabasePath = loaded.KeePassDatabasePath;
        _configuration.KeePassKeyFilePath = loaded.KeePassKeyFilePath;
        _configuration.SealBrowserProfilesOnLock = loaded.SealBrowserProfilesOnLock;
        _configuration.DiagnosticsLogEnabled = loaded.DiagnosticsLogEnabled;
        _configuration.PreferTaskSchedulerAutostart = loaded.PreferTaskSchedulerAutostart;
        _configuration.BackupReminderDays = loaded.BackupReminderDays;
        _configuration.UpdateFeedUrl = loaded.UpdateFeedUrl;
        _configuration.WindowsHelloUnlockEnabled = loaded.WindowsHelloUnlockEnabled;
        _configuration.LastBackupUtc = loaded.LastBackupUtc;
        _configuration.WelcomeChecklistShown = loaded.WelcomeChecklistShown;
        _configuration.ShortcutTipShown = loaded.ShortcutTipShown;

        VaultTimeoutBox.Text = _configuration.VaultLockMinutes.ToString();
        KeePassDbBox.Text = _configuration.KeePassDatabasePath ?? "";
        KeePassKeyBox.Text = _configuration.KeePassKeyFilePath ?? "";
        StartupCheck.IsChecked = _configuration.StartWithWindows;
        UnlockCheck.IsChecked = _configuration.RunOnUnlock;
        LaunchCheck.IsChecked = _configuration.RunOnAppStart;
        TrayCheck.IsChecked = _configuration.MinimizeToTray;
        NotifyCheck.IsChecked = _configuration.TrayNotificationsEnabled;
        DelayBox.Text = _configuration.UnlockDelaySeconds.ToString();
        SealProfilesCheck.IsChecked = _configuration.SealBrowserProfilesOnLock;
        DiagnosticsCheck.IsChecked = _configuration.DiagnosticsLogEnabled;
        TaskSchedulerCheck.IsChecked = _configuration.PreferTaskSchedulerAutostart;
        BackupReminderBox.Text = _configuration.BackupReminderDays.ToString();
        UpdateFeedBox.Text = _configuration.UpdateFeedUrl ?? "";
        ShortcutBox.Text = string.IsNullOrWhiteSpace(_configuration.GlobalShortcut) ? "Ctrl+M" : _configuration.GlobalShortcut;
        RefreshKeePassStatus();
        RefreshSecurityClarity();
        RefreshWorkflowList(_configuration.Workflows.FirstOrDefault());
        _passwordsUnlocked = true;
        _passwordSessionExpiresUtc = DateTimeOffset.UtcNow.Add(PasswordSessionDuration);
        _keepassMasterPassword = null;
    }

    private void RefreshSecurityClarity()
    {
        var hello = WindowsHelloService.IsEnabled ? "on" : "off";
        SecurityClarityText.Text =
            "Encrypted in the vault: workflows + saved site passwords (Argon2id → AES-256-GCM).\n" +
            $"Browser profiles: {(SealProfilesCheck.IsChecked == true ? "sealed (encrypted at rest) when the vault locks" : "left on disk while locked — not recommended")}.\n" +
            "Not encrypted by LaunchFlow: your Windows user account, and KeePassXC .kdbx files (those use KeePass).\n" +
            $"Windows Hello unlock: {hello}.";
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
            RefreshSecurityClarity();
            AppendLog("Windows Hello unlock enabled for this Windows account.");
            MessageBox.Show(this, "Windows Hello unlock is on for this PC.", "Windows Hello");
        }
    }

    private void DisableHello_Click(object sender, RoutedEventArgs e)
    {
        WindowsHelloService.Disable();
        _configuration.WindowsHelloUnlockEnabled = false;
        _store.Save(_configuration);
        RefreshSecurityClarity();
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

    private void MaybeShowBackupReminder()
    {
        if (_configuration.BackupReminderDays <= 0) return;
        DateTimeOffset last = DateTimeOffset.MinValue;
        if (!string.IsNullOrWhiteSpace(_configuration.LastBackupUtc))
            DateTimeOffset.TryParse(_configuration.LastBackupUtc, out last);
        if (last != DateTimeOffset.MinValue &&
            DateTimeOffset.UtcNow - last < TimeSpan.FromDays(_configuration.BackupReminderDays))
            return;

        var answer = MessageBox.Show(this,
            $"It has been a while since the last encrypted vault backup.\n\nCreate a local backup now? (Settings → Security)",
            "Backup reminder", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            BackupVault_Click(this, new RoutedEventArgs());
    }

    private void CheckUpdates_Click(object sender, RoutedEventArgs e) => _ = MaybeCheckUpdatesAsync();

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(VaultBackupService.DataDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = VaultBackupService.DataDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot open folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

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
        else if (ReferenceEquals(sender, PasswordsNav)) MainTabs.SelectedIndex = 1;
        else if (ReferenceEquals(sender, SettingsNav)) MainTabs.SelectedIndex = 2;
        SyncTopNavigation();
    }

    private void SyncTopNavigation()
    {
        WorkflowsNav.IsChecked = MainTabs.SelectedIndex == 0;
        PasswordsNav.IsChecked = MainTabs.SelectedIndex == 1;
        SettingsNav.IsChecked = MainTabs.SelectedIndex == 2;
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs)) return;
        if (_revertingTab) { SyncTopNavigation(); return; }
        if (MainTabs.SelectedIndex == 1)
        {
            if (!PasswordSessionIsValid() && !RequireMasterPassword())
            {
                _revertingTab = true;
                MainTabs.SelectedIndex = 0;
                _revertingTab = false;
                SyncTopNavigation();
                return;
            }
            if (_selected is not null) LoadCredentialEditor(_selected);
            PasswordSessionTimer_Tick(null, EventArgs.Empty);
        }
        else
        {
            // Retain authorization for up to an hour, but don't leave credentials
            // sitting in hidden text controls while browsing other tabs.
            try { ApplyCurrentEditor(); }
            catch (Exception ex) { AppendLog("Could not save credential edits: " + ex.GetType().Name); }
            ClearCredentialEditor();
        }
        SyncTopNavigation();
    }

    private bool RequireMasterPassword(bool forcePrompt = false)
    {
        if (!forcePrompt && PasswordSessionIsValid()) return true;
        if (!_master.IsConfigured) return false;
        var prompt = new UnlockWindow { Owner = this };
        if (prompt.ShowDialog() != true || string.IsNullOrEmpty(prompt.Password)) return false;
        try
        {
            if (_master.Verify(prompt.Password))
            {
                try { ProfileVaultService.UnsealAll(); }
                catch (Exception ex)
                {
                    AppendLog("Could not unseal browser profiles: " + ex.Message);
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

    private void LockPasswords_Click(object sender, RoutedEventArgs e)
    {
        SecureVaultLock("Vault manually locked; master password required before automation.");
    }

    private void ChangeMasterPassword_Click(object sender, RoutedEventArgs e)
    {
        var current = new SecretPromptWindow("Change master password", "Enter your current master password.") { Owner = this };
        if (current.ShowDialog() != true) return;
        var next = new SecretPromptWindow("New master password",
            $"Choose a new password of at least {PasswordStrength.MinimumLength} characters (mixed case, number, or symbol recommended).") { Owner = this };
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
            try { VaultBackupService.CreateBackup(VaultDatabase.Current.DatabasePath); } catch { /* best-effort */ }
            SecureVaultLock("Master password changed; vault locked. Re-enable Windows Hello if you use it.");
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
            Title = "Export LaunchFlow configuration", Filter = "LaunchFlow encrypted backup (*.launchflow)|*.launchflow",
            DefaultExt = ".launchflow", FileName = "My-LaunchFlow-Backup.launchflow"
        };
        if (dialog.ShowDialog(this) != true) return;
        var first = new SecretPromptWindow("Encrypt backup",
            $"Set an export password ({PasswordStrength.MinimumLength}+ characters). You'll need it on the new PC.") { Owner = this };
        if (first.ShowDialog() != true) return;
        var second = new SecretPromptWindow("Confirm export password", "Enter the export password again to avoid losing access to the backup.") { Owner = this };
        if (second.ShowDialog() != true) return;
        if (first.Secret != second.Secret) { MessageBox.Show(this, "Export passwords did not match."); return; }
        try
        {
            SecureTransferService.Export(dialog.FileName, first.Secret, _configuration, _vault, IncludePasswordsCheck.IsChecked == true);
            AppendLog("Encrypted portable configuration exported.");
            MessageBox.Show(this, "Encrypted backup saved. Store the file and its password securely.", "Export complete");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_runner.IsRunning) { MessageBox.Show(this, "Stop active workflows before importing."); return; }
        if (!RequireMasterPassword(forcePrompt: true)) return;
        var file = new OpenFileDialog
        {
            Title = "Import a LaunchFlow encrypted backup", Filter = "LaunchFlow encrypted backup (*.launchflow)|*.launchflow"
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

        MainTabs.SelectedIndex = 2; // Settings
        try { SettingsTabs.SelectedIndex = SettingsTabs.Items.Count - 1; } catch { /* optional */ }
        var prompt = new SecretPromptWindow("Decrypt backup",
            "Enter the password used when exporting this .launchflow backup.")
        { Owner = this };
        if (prompt.ShowDialog() != true) return;
        try
        {
            var imported = SecureTransferService.Read(path, prompt.Secret);
            if (MessageBox.Show(this,
                    $"Replace your {_configuration.Workflows.Count} existing workflow(s) with {imported.Config.Workflows.Count} imported workflow(s) and their credentials?\n\n" +
                    "File:\n" + path + "\n\nExisting workflows/passwords will be replaced.",
                    "Confirm import", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            // Database transaction replaces both workflows and encrypted credentials
            // atomically; partial import never corrupts existing settings.
            VaultDatabase.Current.ReplaceSnapshot(imported.Config, imported.Credentials);
            _configuration.Workflows.Clear();
            _configuration.Workflows.AddRange(imported.Config.Workflows);
            _configuration.StartWithWindows = imported.Config.StartWithWindows;
            _configuration.RunOnUnlock = imported.Config.RunOnUnlock;
            _configuration.RunOnAppStart = imported.Config.RunOnAppStart;
            _configuration.MinimizeToTray = imported.Config.MinimizeToTray;
            _configuration.TrayNotificationsEnabled = imported.Config.TrayNotificationsEnabled;
            _configuration.UnlockDelaySeconds = imported.Config.UnlockDelaySeconds;
            _configuration.ShortcutTipShown = imported.Config.ShortcutTipShown;
            // No automatic registration of imported startup settings or hotkeys.
            _configuration.GlobalShortcut = _activeShortcut;
            _configuration.VaultLockMinutes = Math.Clamp(imported.Config.VaultLockMinutes, 1, 120);
            VaultTimeoutBox.Text = _configuration.VaultLockMinutes.ToString();
            StartupCheck.IsChecked = _configuration.StartWithWindows;
            UnlockCheck.IsChecked = _configuration.RunOnUnlock;
            LaunchCheck.IsChecked = _configuration.RunOnAppStart;
            TrayCheck.IsChecked = _configuration.MinimizeToTray;
            NotifyCheck.IsChecked = _configuration.TrayNotificationsEnabled;
            DelayBox.Text = _configuration.UnlockDelaySeconds.ToString();
            RefreshWorkflowList(_configuration.Workflows.FirstOrDefault());
            AppendLog("Imported configuration from " + path);
            MessageBox.Show(this,
                "Import complete. Imported workflows are disabled for safety until you review and enable them. Check Settings → General and save to apply startup preferences.",
                "Import complete");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        // The tray menu's explicit Exit action (or Windows shutdown) must not
        // show the dialog or secretly keep the process running.
        if (_exiting || System.Windows.Application.Current.Dispatcher.HasShutdownStarted)
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

        // Never pretend unsaved edits have persisted. Keep the window open if
        // the vault is unlocked but saving fails.
        if (VaultDatabase.Current.IsUnlocked && !SaveAll())
        {
            e.Cancel = true;
            return;
        }

        if (choice.Choice == CloseChoice.Background)
        {
            e.Cancel = true;
            Hide();
            AppendLog("Running in the system tray. Reopen from its icon or the desktop shortcut; choose Exit in the tray menu to stop LaunchFlow.");
            return;
        }

        // Exit completely: OnClosed disposes the tray icon, cancels triggers,
        // and tears down automation/browser connections.
        _exiting = true;
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
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
        try { await _runner.DisposeAsync(); } catch { /* closed during shutdown */ }
        _runCancellation?.Dispose();
        _unlockCancellation?.Dispose();
    }
}
