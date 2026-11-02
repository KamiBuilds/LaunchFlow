namespace LaunchFlow.Models;

/// <summary>
/// Immutable values captured from the WPF editor. Never contains a password,
/// WPF control, live mutable collection, or an encrypted vault key.
/// </summary>
public sealed record WebsiteEditDraft(
    int Index,
    Guid ExpectedId,
    bool Enabled,
    string Name,
    string StartUrl,
    string DestinationUrl,
    string Browser,
    bool ReplaceMonitor,
    int MonitorIndex,
    string MonitorDeviceName,
    string LoginMode,
    string UsernameContinueSelector,
    string PasswordCredentialOrigin,
    string KeepassEntryPath,
    string UsernameSelector,
    string PasswordSelector,
    string SubmitSelector,
    string CredentialOrigin,
    string LoginSuccessSelector,
    string LoginSuccessUrlContains,
    int LoginWaitSeconds)
{
    public void Apply(WebsiteWorkflow workflow)
    {
        if (workflow.Id != ExpectedId)
            throw new InvalidOperationException("The selected website was replaced during editing.");
        workflow.Enabled = Enabled;
        workflow.Name = Name;
        workflow.StartUrl = StartUrl;
        workflow.DestinationUrl = DestinationUrl;
        workflow.Browser = Browser;
        if (ReplaceMonitor)
        {
            workflow.MonitorIndex = MonitorIndex;
            workflow.MonitorDeviceName = MonitorDeviceName;
        }
        workflow.LoginMode = LoginMode;
        workflow.AutoLogin = Services.WebsiteLoginMode.UsesSavedPassword(LoginMode);
        workflow.UsernameContinueSelector = UsernameContinueSelector;
        workflow.PasswordCredentialOrigin = PasswordCredentialOrigin;
        workflow.KeepassEntryPath = KeepassEntryPath;
        workflow.UsernameSelector = UsernameSelector;
        workflow.PasswordSelector = PasswordSelector;
        workflow.SubmitSelector = SubmitSelector;
        workflow.CredentialOrigin = CredentialOrigin;
        workflow.LoginSuccessSelector = LoginSuccessSelector;
        workflow.LoginSuccessUrlContains = LoginSuccessUrlContains;
        workflow.LoginWaitSeconds = LoginWaitSeconds;
    }
}

public sealed record ConfigurationEditDraft(
    WebsiteEditDraft? Website,
    string UiLanguage,
    bool StartWithWindows,
    bool RunOnUnlock,
    bool RunOnAppStart,
    bool MinimizeToTray,
    bool TrayNotificationsEnabled,
    bool SealBrowserProfilesOnLock,
    bool PreferTaskSchedulerAutostart,
    string? UpdateFeedUrl,
    int UnlockDelaySeconds,
    int VaultLockMinutes,
    string GlobalShortcut,
    string KeePassDatabasePath,
    string KeePassKeyFilePath,
    bool ShortcutTipShown)
{
    public void Apply(Configuration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (Website is not null)
        {
            if (Website.Index < 0 || Website.Index >= configuration.Workflows.Count)
                throw new InvalidOperationException("Website selection is no longer current.");
            Website.Apply(configuration.Workflows[Website.Index]);
        }
        configuration.UiLanguage = UiLanguage;
        configuration.StartWithWindows = StartWithWindows;
        configuration.RunOnUnlock = RunOnUnlock;
        configuration.RunOnAppStart = RunOnAppStart;
        configuration.MinimizeToTray = MinimizeToTray;
        configuration.TrayNotificationsEnabled = TrayNotificationsEnabled;
        configuration.SealBrowserProfilesOnLock = SealBrowserProfilesOnLock;
        configuration.DiagnosticsLogEnabled = false;
        configuration.PreferTaskSchedulerAutostart = PreferTaskSchedulerAutostart;
        configuration.UpdateFeedUrl = UpdateFeedUrl;
        configuration.UnlockDelaySeconds = UnlockDelaySeconds;
        configuration.VaultLockMinutes = VaultLockMinutes;
        configuration.GlobalShortcut = GlobalShortcut;
        configuration.KeePassDatabasePath = KeePassDatabasePath;
        configuration.KeePassKeyFilePath = KeePassKeyFilePath;
        configuration.ShortcutTipShown = ShortcutTipShown;
    }
}
