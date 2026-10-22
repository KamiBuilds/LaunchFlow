using System.IO;
using Microsoft.Data.Sqlite;

namespace LaunchFlow.Services;

/// <summary>
/// Rotating copies of the already-encrypted SQLite vault file.
/// Backups are ciphertext on disk — no master password is written.
/// </summary>
public static class VaultBackupService
{
    public const int KeepCount = 5;

    public static string BackupDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaunchFlow", "backups");

    public static string DataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaunchFlow");

    /// <summary>
    /// Copy <paramref name="databasePath"/> into the backup folder. Safe while the
    /// app holds the file open because SQLite uses a shared read for a File.Copy
    /// of a consistent WAL checkpoint; we checkpoint first when possible.
    /// </summary>
    public static string CreateBackup(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            throw new FileNotFoundException("Encrypted vault database was not found.", databasePath);

        Directory.CreateDirectory(BackupDirectory);
        VaultDatabase.Current.Checkpoint();

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dest = Path.Combine(BackupDirectory, $"launchflow-{stamp}.db");
        File.Copy(databasePath, dest, overwrite: false);
        PruneOldBackups();
        return dest;
    }

    public static IReadOnlyList<string> ListBackups()
    {
        if (!Directory.Exists(BackupDirectory)) return Array.Empty<string>();
        return Directory.GetFiles(BackupDirectory, "launchflow-*.db")
            .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Replace the live vault DB with an encrypted backup copy. The vault must be
    /// locked first. Caller unlocks with the master password afterward.
    /// Returns the safety copy of the previous live DB (if any), or null.
    /// </summary>
    public static string? RestoreEncryptedDatabase(string backupDatabasePath, string liveDatabasePath)
    {
        if (string.IsNullOrWhiteSpace(backupDatabasePath) || !File.Exists(backupDatabasePath))
            throw new FileNotFoundException("Backup database was not found.", backupDatabasePath);
        if (VaultDatabase.Current.IsUnlocked)
            throw new InvalidOperationException("Lock the vault before restoring a database backup.");

        // Drop pooled connections so the live .db file can be overwritten.
        SqliteConnection.ClearAllPools();

        var liveDir = Path.GetDirectoryName(liveDatabasePath)
            ?? throw new InvalidOperationException("Invalid vault path.");
        Directory.CreateDirectory(liveDir);

        string? safetyCopy = null;
        if (File.Exists(liveDatabasePath))
        {
            Directory.CreateDirectory(BackupDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            safetyCopy = Path.Combine(BackupDirectory, $"launchflow-before-restore-{stamp}.db");
            File.Copy(liveDatabasePath, safetyCopy, overwrite: false);
        }

        TryDelete(liveDatabasePath + "-wal");
        TryDelete(liveDatabasePath + "-shm");
        File.Copy(backupDatabasePath, liveDatabasePath, overwrite: true);
        return safetyCopy;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* WAL/SHM may be absent */ }
    }

    private static void PruneOldBackups()
    {
        var files = Directory.GetFiles(BackupDirectory, "launchflow-*.db")
            .Where(f => !Path.GetFileName(f).Contains("before-restore", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
            .Skip(KeepCount)
            .ToList();
        foreach (var file in files)
        {
            try { File.Delete(file); }
            catch { /* leave older backups if locked */ }
        }
    }
}
