using System.IO;

namespace LaunchFlow.Services;

/// <summary>
/// Destructive recovery when the master password is lost.
/// Deletes the local vault DB and sealed profiles; cannot decrypt old data.
/// </summary>
public static class VaultResetService
{
    public sealed record ResetResult(bool DatabaseRemoved, bool ProfilesRemoved, IReadOnlyList<Exception> Failures)
    {
        public bool Succeeded => Failures.Count == 0;
        public bool Partial => !Succeeded && (DatabaseRemoved || ProfilesRemoved);
    }

    public static ResetResult ResetLocalVault(bool wipeProfiles) => ResetLocalVault(
        wipeProfiles, File.Delete, () => ProfileVaultService.WipeAllProfilesStrict());

    // Scoped fault injection for portable tests. The application's public path
    // always uses actual file/profile deletion and has no runtime test flag.
    internal static ResetResult ResetLocalVault(
        bool wipeProfiles, Action<string> deleteFile,
        Func<IReadOnlyList<Exception>> deleteProfiles)
    {
        ArgumentNullException.ThrowIfNull(deleteFile);
        ArgumentNullException.ThrowIfNull(deleteProfiles);
        try { VaultDatabase.Current.Lock(); } catch { /* ignore */ }

        var failures = new List<Exception>();
        var databaseRemoved = false;
        try
        {
            TryDelete(VaultDatabase.Current.DatabasePath, deleteFile);
            TryDelete(VaultDatabase.Current.DatabasePath + "-wal", deleteFile);
            TryDelete(VaultDatabase.Current.DatabasePath + "-shm", deleteFile);
            databaseRemoved = !File.Exists(VaultDatabase.Current.DatabasePath);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
            databaseRemoved = !File.Exists(VaultDatabase.Current.DatabasePath);
        }

        // Never disable Hello or wipe profiles while the vault DB is still present
        // after a failed delete — that would strand an intact vault without unlock
        // recovery material / browser sessions.
        var profilesRemoved = !wipeProfiles;
        if (!databaseRemoved)
        {
            profilesRemoved = false;
            if (failures.Count == 0)
                failures.Add(new IOException("Vault database is still present after reset."));
        }
        else
        {
            WindowsHelloService.Disable();
            if (wipeProfiles)
            {
                var profileOutcome = deleteProfiles();
                profilesRemoved = profileOutcome.Count == 0;
                failures.AddRange(profileOutcome);
            }
        }

        if (failures.Count == 0)
        {
            CrashLog.Write("VaultReset", "Local vault was reset by the user after a lost master password.");
            return new ResetResult(databaseRemoved, profilesRemoved, Array.Empty<Exception>());
        }

        CrashLog.Write("VaultResetPartial",
            $"Reset incomplete: databaseRemoved={databaseRemoved}; profilesRemoved={profilesRemoved}; failures={failures.Count}.");
        throw new AggregateException(
            databaseRemoved
                ? "The vault database was removed, but some browser profile cleanup failed. Remaining profile folders must be cleaned before treating reset as complete."
                : "LaunchFlow could not complete the requested reset. Close other apps using these files and retry. Browser profiles were left unchanged.",
            failures);
    }

    private static void TryDelete(string path, Action<string> deleteFile)
    {
        try
        {
            if (File.Exists(path)) deleteFile(path);
        }
        catch (Exception ex)
        {
            CrashLog.Write("VaultResetDelete", ex);
            throw new IOException($"Could not delete '{path}'. Close LaunchFlow and try again.", ex);
        }
    }
}
