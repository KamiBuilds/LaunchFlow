using System.IO;

namespace LaunchFlow.Services;

/// <summary>
/// Destructive recovery when the master password is lost.
/// Deletes the local vault DB and sealed profiles; cannot decrypt old data.
/// </summary>
public static class VaultResetService
{
    public static void ResetLocalVault(bool wipeProfiles)
    {
        try { VaultDatabase.Current.Lock(); } catch { /* ignore */ }
        WindowsHelloService.Disable();

        TryDelete(VaultDatabase.Current.DatabasePath);
        TryDelete(VaultDatabase.Current.DatabasePath + "-wal");
        TryDelete(VaultDatabase.Current.DatabasePath + "-shm");

        if (wipeProfiles)
            ProfileVaultService.WipeAllProfiles();


        CrashLog.Write("VaultReset", "Local vault was reset by the user after a lost master password.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            CrashLog.Write("VaultResetDelete", ex);
            throw new IOException($"Could not delete '{path}'. Close LaunchFlow and try again.", ex);
        }
    }
}
