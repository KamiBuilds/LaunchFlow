using System.Text.Json;
using LaunchFlow.Models;
using LaunchFlow.Services;

internal static class SafetyIntegrationTests
{
    public static IEnumerable<(string Name, Action Body)> Cases()
    {
        yield return ("credential deletion cannot retarget after selection changes", () =>
        {
            var first = new WebsiteWorkflow { Name = "Original" };
            var other = new WebsiteWorkflow { Name = "Imported" };
            var original = new Configuration { Workflows = new() { first } };
            Check(CredentialDeleteGuard.IsCurrent(original, first, first, 7, 7, true),
                "unchanged selected workflow should authorize its own credential deletion");
            Check(!CredentialDeleteGuard.IsCurrent(original, other, first, 7, 7, true),
                "selection replaced during confirmation must never redirect deletion");
            Check(!CredentialDeleteGuard.IsCurrent(original, first, first, 7, 8, true),
                "import revision changed during confirmation must cancel deletion");
            Check(!CredentialDeleteGuard.IsCurrent(
                new Configuration { Workflows = new() { other } }, other, first, 7, 7, true),
                "imported workflow must not inherit the previous identity");
            Check(!CredentialDeleteGuard.IsCurrent(
                new Configuration { Workflows = new() { new WebsiteWorkflow { Id = first.Id } } },
                first, first, 7, 7, true),
                "GUID collision cannot replace original reference ownership");
            Check(!CredentialDeleteGuard.IsCurrent(original, first, first, 7, 7, false),
                "vault lock, session expiry or administrator policy revocation cancels deletion");
        });
        yield return ("import projects settings consistently to storage and live state", () =>
        {
            var old = new Configuration {
                StartWithWindows = true, PreferTaskSchedulerAutostart = true,
                GlobalShortcut = "Ctrl+K", WindowsHelloUnlockEnabled = true,
                UpdateFeedUrl = "https://existing-updates.example/feed",
                KeePassDatabasePath = "old.kdbx", SealBrowserProfilesOnLock = true
            };
            var incoming = new Configuration {
                KeePassDatabasePath = "new-imported.kdbx",
                KeePassKeyFilePath = "new-imported.key",
                SealBrowserProfilesOnLock = false,
                StartWithWindows = false, PreferTaskSchedulerAutostart = false,
                GlobalShortcut = "Alt+Q", WindowsHelloUnlockEnabled = false,
                UpdateFeedUrl = "https://untrusted-updates.example/feed",
                RunOnUnlock = true, RunOnAppStart = true,
                UnlockDelaySeconds = 11, VaultLockMinutes = 19,
                UiLanguage = "nl",
                Workflows = new() { new WebsiteWorkflow { Name = "Imported login" } }
            };
            var projected = ImportConfigurationProjection.ForThisDevice(incoming, old, "Ctrl+M");
            var cold = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(projected))!;
            Check(projected.KeePassDatabasePath == incoming.KeePassDatabasePath &&
                cold.KeePassDatabasePath == incoming.KeePassDatabasePath &&
                projected.KeePassKeyFilePath == incoming.KeePassKeyFilePath &&
                cold.KeePassKeyFilePath == incoming.KeePassKeyFilePath,
                "imported KeePass paths must match the cold persisted snapshot");
            Check(!projected.SealBrowserProfilesOnLock && !cold.SealBrowserProfilesOnLock,
                "imported profile-sealing preference must not revert on normal Save");
            Check(projected.StartWithWindows && projected.PreferTaskSchedulerAutostart &&
                projected.GlobalShortcut == "Ctrl+M" && projected.WindowsHelloUnlockEnabled &&
                projected.UpdateFeedUrl == old.UpdateFeedUrl &&
                cold.StartWithWindows && cold.GlobalShortcut == "Ctrl+M",
                "portable data must not silently register imported machine-local startup/hotkey/update settings");
            Check(projected.RunOnUnlock && projected.RunOnAppStart &&
                projected.UiLanguage == "nl" && projected.VaultLockMinutes == 19 &&
                projected.Workflows.Single().Name == "Imported login",
                "portable workflow and non-device settings are preserved");
        });
    }

    private static void Check(bool ok, string detail)
    {
        if (!ok) throw new InvalidOperationException(detail);
    }
}
