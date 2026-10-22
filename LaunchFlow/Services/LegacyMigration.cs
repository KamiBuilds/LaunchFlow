using LaunchFlow.Models;

namespace LaunchFlow.Services;

/// <summary>
/// Stub for v0.5.x DPAPI migration. Returns no legacy data in this source drop;
/// App.xaml.cs still calls these members for the upgrade path.
/// </summary>
public static class LegacyMigration
{
    public static bool HasLegacy => false;

    public static bool VerifyOldMaster(string password) => false;

    public static (Configuration Config, Dictionary<Guid, SiteCredential> Secrets) ReadLegacyData() =>
        throw new InvalidOperationException(
            "No legacy LaunchFlow data is available in this environment. " +
            "If you are upgrading from v0.5.x on Windows, ensure settings.json / vault.json / security.json " +
            "exist under %LOCALAPPDATA%\\LaunchFlow and use a build that includes the migration reader.");
}
