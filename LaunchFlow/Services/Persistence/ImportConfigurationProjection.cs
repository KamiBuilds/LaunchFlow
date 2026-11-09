using LaunchFlow.Models;

namespace LaunchFlow.Services;

/// <summary>
/// Single source of truth for portable import. The same object goes to
/// ReplaceSnapshot, the live MainWindow state and its settings controls.
/// Device-local startup, hotkey, Windows Hello and update-source preferences
/// cannot be silently registered or replaced from a portable archive.
/// </summary>
public static class ImportConfigurationProjection
{
    public static Configuration ForThisDevice(Configuration imported, Configuration current,
        string activeShortcut)
    {
        ArgumentNullException.ThrowIfNull(imported);
        ArgumentNullException.ThrowIfNull(current);
        return new Configuration
        {
            Workflows = imported.Workflows,
            Workspaces = imported.Workspaces,
            UiLanguage = imported.UiLanguage,
            AdditionalSettings = imported.AdditionalSettings,
            // Operating-system registered values remain tied to this device.
            StartWithWindows = current.StartWithWindows,
            PreferTaskSchedulerAutostart = current.PreferTaskSchedulerAutostart,
            GlobalShortcut = activeShortcut,
            WindowsHelloUnlockEnabled = current.WindowsHelloUnlockEnabled,
            UpdateFeedUrl = current.UpdateFeedUrl,
            // Other portable preferences are imported and displayed verbatim.
            RunOnUnlock = imported.RunOnUnlock,
            RunOnAppStart = imported.RunOnAppStart,
            MinimizeToTray = imported.MinimizeToTray,
            UnlockDelaySeconds = Math.Clamp(imported.UnlockDelaySeconds, 0, 300),
            VaultLockMinutes = Math.Clamp(imported.VaultLockMinutes, 1, 120),
            ShortcutTipShown = imported.ShortcutTipShown,
            TrayNotificationsEnabled = imported.TrayNotificationsEnabled,
            WelcomeChecklistShown = imported.WelcomeChecklistShown,
            DiagnosticsLogEnabled = false,
            SealBrowserProfilesOnLock = imported.SealBrowserProfilesOnLock,
            KeePassDatabasePath = imported.KeePassDatabasePath,
            KeePassKeyFilePath = imported.KeePassKeyFilePath
        };
    }
}
