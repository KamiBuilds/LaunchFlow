using System.IO;
using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;

namespace LaunchFlow.Services;

/// <summary>
/// Operator recovery for incomplete/corrupt vault files and verified SQLite snapshots
/// under the vault data directory's recovery folder. Live files are moved aside only
/// after a verified backup has been staged next to them. Rollback restores only what
/// this attempt actually moved or promoted — never deletes an untouched original.
/// </summary>
public static class VaultRecoveryService
{
    public static string RecoveryDirectory =>
        Path.Combine(VaultDatabase.Current.DataDirectory, "recovery");

    public static IReadOnlyList<string> ListSqliteRecoveryCopies()
    {
        var root = RecoveryDirectory;
        if (!Directory.Exists(root)) return Array.Empty<string>();
        return Directory.GetFiles(root, "*.db")
            .Concat(Directory.GetFiles(root, "*.sqlite"))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
    }

    public static VaultStorageState InspectCopy(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            return VaultStorageState.Absent;
        return new VaultDatabase(databasePath).InspectStorage();
    }

    /// <summary>
    /// Stage a verified backup, quarantine the live vault, then promote the stage.
    /// Caller must unlock afterward. Returns the quarantine path of the broken file.
    /// </summary>
    public static string RestoreSqliteBackup(string backupPath)
    {
        try { VaultDatabase.Current.Lock(); } catch { /* revoke best-effort */ }
        return RestoreSqliteBackup(
            backupPath,
            VaultDatabase.Current.DatabasePath,
            VaultDatabase.Current.DataDirectory,
            VaultRestoreFileOperations.Default);
    }

    public static string RestoreSqliteBackup(string backupPath, string liveDatabasePath, string dataDirectory) =>
        RestoreSqliteBackup(backupPath, liveDatabasePath, dataDirectory, VaultRestoreFileOperations.Default);

    /// <summary>
    /// Path-explicit restore used by harnesses and the Current-backed entry point.
    /// <paramref name="fileOps"/> is the restore-only injection seam for fault tests.
    /// </summary>
    public static string RestoreSqliteBackup(
        string backupPath,
        string liveDatabasePath,
        string dataDirectory,
        VaultRestoreFileOperations fileOps)
    {
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
            throw new FileNotFoundException("Recovery backup was not found.", backupPath);
        if (string.IsNullOrWhiteSpace(liveDatabasePath))
            throw new ArgumentException("Live vault path is required.", nameof(liveDatabasePath));
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Vault data directory is required.", nameof(dataDirectory));
        ArgumentNullException.ThrowIfNull(fileOps);

        // Refuse before creating recovery/stage paths so a junk file cannot
        // rearrange the live vault directory layout.
        var state = InspectCopy(backupPath);
        if (state is not (VaultStorageState.PresentNeedsAuthentication
            or VaultStorageState.LegacyUpgradeRequired))
            throw new InvalidDataException(
                "That file is not a readable LaunchFlow vault backup.");

        Directory.CreateDirectory(dataDirectory);
        var recoveryDirectory = Path.Combine(dataDirectory, "recovery");
        Directory.CreateDirectory(recoveryDirectory);
        SqliteConnection.ClearAllPools();

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" +
            Guid.NewGuid().ToString("N");
        var quarantine = Path.Combine(recoveryDirectory, "broken-" + stamp + ".db");
        var staged = Path.Combine(dataDirectory, ".restore-stage-" + stamp + ".db");
        var progress = new RestoreProgress();

        try
        {
            // 1) Copy backup into a sibling stage and inspect it before touching live.
            CopyOwnedStage(fileOps, backupPath, staged, ref progress.StagedDb);
            if (fileOps.FileExists(backupPath + "-wal"))
                CopyOwnedStage(fileOps, backupPath + "-wal", staged + "-wal", ref progress.StagedWal);
            if (fileOps.FileExists(backupPath + "-shm"))
                CopyOwnedStage(fileOps, backupPath + "-shm", staged + "-shm", ref progress.StagedShm);

            var stagedState = new VaultDatabase(staged).InspectStorage();
            if (stagedState is VaultStorageState.IncompleteOrUnsupported or VaultStorageState.Absent)
                throw new InvalidDataException(
                    "Staged recovery copy still looks incomplete; live vault was left untouched.");

            // 2) Quarantine the live set only after the stage is verified.
            if (MoveIfExists(fileOps, liveDatabasePath, quarantine))
                progress.QuarantinedDb = true;
            if (MoveIfExists(fileOps, liveDatabasePath + "-wal", quarantine + "-wal"))
                progress.QuarantinedWal = true;
            if (MoveIfExists(fileOps, liveDatabasePath + "-shm", quarantine + "-shm"))
                progress.QuarantinedShm = true;

            // 3) Promote the stage into the live paths.
            fileOps.MoveFile(staged, liveDatabasePath);
            progress.StagedDb = false;
            progress.PromotedDb = true;
            if (MoveIfExists(fileOps, staged + "-wal", liveDatabasePath + "-wal"))
            {
                progress.StagedWal = false;
                progress.PromotedWal = true;
            }
            if (MoveIfExists(fileOps, staged + "-shm", liveDatabasePath + "-shm"))
            {
                progress.StagedShm = false;
                progress.PromotedShm = true;
            }
        }
        catch (Exception primary)
        {
            RethrowAfterRollback(primary, fileOps, liveDatabasePath, quarantine, staged, progress);
            throw; // unreachable; keeps definite-assignment analyzers calm
        }

        CleanupOwnedStaging(fileOps, staged, progress);

        var restored = new VaultDatabase(liveDatabasePath).InspectStorage();
        if (restored is VaultStorageState.IncompleteOrUnsupported or VaultStorageState.Absent)
        {
            var validation = new InvalidDataException(
                "Restored file still looks incomplete. The previous vault was kept at:\n" + quarantine);
            RethrowAfterRollback(validation, fileOps, liveDatabasePath, quarantine, staged, progress);
        }

        CrashLog.Write("VaultRestore",
            "Restored SQLite vault from backup; previous file quarantined at " + quarantine);
        return quarantine;
    }

    /// <summary>
    /// Quarantine a broken live vault so first-run setup or a portable import can proceed.
    /// </summary>
    public static string QuarantineLiveVault()
    {
        try { VaultDatabase.Current.Lock(); } catch { /* best effort */ }
        return QuarantineLiveVault(
            VaultDatabase.Current.DatabasePath,
            VaultDatabase.Current.DataDirectory);
    }

    public static string QuarantineLiveVault(string liveDatabasePath, string dataDirectory) =>
        QuarantineLiveVault(liveDatabasePath, dataDirectory, VaultRestoreFileOperations.Default);

    public static string QuarantineLiveVault(string liveDatabasePath, string dataDirectory,
        VaultRestoreFileOperations fileOps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(fileOps);
        if (HasIncompletePreservation(dataDirectory))
            throw new InvalidOperationException("A previous vault preservation operation needs manual recovery before retrying.");
        RequireIncompleteCandidate(liveDatabasePath);
        SqliteConnection.ClearAllPools();
        var recoveryDirectory = Path.Combine(dataDirectory, "recovery");
        Directory.CreateDirectory(recoveryDirectory);
        if ((File.GetAttributes(recoveryDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The recovery directory must not be a link.");
        var quarantine = Path.Combine(recoveryDirectory,
            "broken-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" +
            Guid.NewGuid().ToString("N") + ".db");
        var marker = Path.Combine(dataDirectory, PreservationMarkerName);
        // Persist intent before the first move: interruption must not masquerade as an absent vault.
        using (var intent = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            intent.Write(System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(quarantine)));
            intent.Flush(flushToDisk: true);
        }
        var moved = new List<string>();
        try
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                if (suffix != "" && !fileOps.FileExists(liveDatabasePath + suffix)) continue;
                // File.Move is no-clobber. Never remove an occupied destination, even on rollback.
                fileOps.MoveFile(liveDatabasePath + suffix, quarantine + suffix);
                moved.Add(suffix);
            }
            // A file may have been replaced after admission. Inspect the actual preserved set
            // before permitting fresh setup, not just the earlier live-path snapshot.
            RequireIncompleteCandidate(quarantine);
            File.Delete(marker);
            return quarantine;
        }
        catch (Exception primary)
        {
            try
            {
                foreach (var suffix in moved)
                    RestoreOriginal(fileOps, quarantine + suffix, liveDatabasePath + suffix);
                File.Delete(marker);
            }
            catch (Exception rollback)
            {
                throw new AggregateException("Vault preservation did not complete. Retain both live and recovery files; no automatic setup is permitted.", primary, rollback);
            }
            ExceptionDispatchInfo.Capture(primary).Throw();
            throw;
        }
    }

    private const string PreservationMarkerName = ".vault-preservation-pending";

    internal static bool HasIncompletePreservation(string dataDirectory)
    {
        try { _ = File.GetAttributes(Path.Combine(dataDirectory, PreservationMarkerName)); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static bool CanStartFreshVault(string path) =>
        !HasIncompletePreservation(Path.GetDirectoryName(Path.GetFullPath(path))!) &&
        InspectQuarantineCandidate(path) == VaultStorageState.IncompleteOrUnsupported;

    private static void RequireIncompleteCandidate(string path)
    {
        if (InspectQuarantineCandidate(path) != VaultStorageState.IncompleteOrUnsupported)
            throw new InvalidOperationException("Only incomplete or unrecognized vault material can be preserved for fresh setup. Recognized vaults require authentication.");
    }

    // Inspect an owned snapshot, not the source: even a read-only SQLite open can change sidecars.
    private static VaultStorageState InspectQuarantineCandidate(string path)
    {
        path = Path.GetFullPath(path);
        var stage = Directory.CreateTempSubdirectory("LaunchFlow-VaultInspection-").FullName;
        VaultStorageState result;
        try
        {
            var streams = new List<FileStream>();
            try
            {
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var source = path + suffix;
                    if (suffix != "" && !File.Exists(source)) continue;
                    if ((File.GetAttributes(source) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                        throw new IOException("Vault recovery requires ordinary files, not links or directories.");
                    streams.Add(new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read));
                }
                foreach (var stream in streams)
                {
                    var destination = Path.Combine(stage, "candidate.db" + stream.Name[path.Length..]);
                    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    stream.CopyTo(output);
                }
            }
            finally { foreach (var stream in streams) stream.Dispose(); }
            var snapshot = Path.Combine(stage, "candidate.db");
            result = new VaultDatabase(snapshot).InspectStorage();
            if (result == VaultStorageState.IncompleteOrUnsupported && new FileInfo(snapshot).Length > 0)
            {
                try
                {
                    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = snapshot, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                    connection.Open();
                    foreach (var table in new[] { "secure_meta", "vault_meta" })
                    {
                        using var exists = connection.CreateCommand();
                        exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name;";
                        exists.Parameters.AddWithValue("$name", table);
                        if (exists.ExecuteScalar() is null) continue;
                        using var owner = connection.CreateCommand();
                        // Fixed identifiers only. Even invalid metadata rows are evidence of prior ownership.
                        owner.CommandText = "SELECT 1 FROM " + table + " LIMIT 1;";
                        if (owner.ExecuteScalar() is not null) result = VaultStorageState.PresentNeedsAuthentication;
                    }
                }
                catch (SqliteException)
                {
                    // Unrecognized bytes remain unknown; they are preserved in full, never overwritten.
                }
            }
        }
        catch (Exception primary)
        {
            try { Directory.Delete(stage, recursive: true); }
            catch (Exception cleanup) { throw new AggregateException("Vault inspection failed and its owned snapshot could not be removed.", primary, cleanup); }
            throw;
        }
        Directory.Delete(stage, recursive: true);
        return result;
    }

    public static void OpenRecoveryFolder()
    {
        Directory.CreateDirectory(RecoveryDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = RecoveryDirectory,
            UseShellExecute = true
        });
    }

    private static void RethrowAfterRollback(
        Exception primary,
        VaultRestoreFileOperations fileOps,
        string live,
        string quarantine,
        string staged,
        RestoreProgress progress)
    {
        Exception? rollbackFailure = null;
        Exception? cleanupFailure = null;
        try { RollbackRestore(fileOps, live, quarantine, progress); }
        catch (Exception error) { rollbackFailure = error; }
        try { CleanupOwnedStaging(fileOps, staged, progress); }
        catch (Exception error) { cleanupFailure = error; }

        if (rollbackFailure is not null || cleanupFailure is not null)
        {
            var failures = new List<Exception> { primary };
            if (rollbackFailure is not null) failures.Add(rollbackFailure);
            if (cleanupFailure is not null) failures.Add(cleanupFailure);
            throw new AggregateException(
                "Vault restore failed and recovery is incomplete. Inspect retained originals at " +
                quarantine + " and remaining staging files at " + staged + ".", failures);
        }
        ExceptionDispatchInfo.Capture(primary).Throw();
    }

    /// <summary>
    /// Remove all promoted replacements before restoring any quarantined original.
    /// If removal is refused, leave the entire original set in quarantine rather
    /// than mixing old WAL/SHM companions with a replacement main database.
    /// </summary>
    private static void RollbackRestore(
        VaultRestoreFileOperations fileOps,
        string live,
        string quarantine,
        RestoreProgress progress)
    {
        SqliteConnection.ClearAllPools();
        if (progress.PromotedDb) DeletePromoted(fileOps, live);
        if (progress.PromotedWal) DeletePromoted(fileOps, live + "-wal");
        if (progress.PromotedShm) DeletePromoted(fileOps, live + "-shm");

        if (progress.QuarantinedDb) RestoreOriginal(fileOps, quarantine, live);
        if (progress.QuarantinedWal) RestoreOriginal(fileOps, quarantine + "-wal", live + "-wal");
        if (progress.QuarantinedShm) RestoreOriginal(fileOps, quarantine + "-shm", live + "-shm");
    }

    private static void DeletePromoted(VaultRestoreFileOperations fileOps, string path)
    {
        if (!fileOps.FileExists(path)) return;
        fileOps.DeleteFile(path);
        if (fileOps.FileExists(path))
            throw new IOException("Promoted replacement could not be removed: " + path);
    }

    private static void RestoreOriginal(VaultRestoreFileOperations fileOps, string from, string to)
    {
        if (!fileOps.FileExists(from))
            throw new IOException("Quarantined original is missing: " + from);
        if (fileOps.FileExists(to))
            throw new IOException("Original cannot be restored over an occupied live path: " + to);
        fileOps.MoveFile(from, to);
    }

    // Record destination ownership before copying. A failed file copy can
    // already have written encrypted bytes to its destination.
    private static void CopyOwnedStage(
        VaultRestoreFileOperations fileOps, string source, string destination, ref bool owned)
    {
        if (fileOps.FileExists(destination))
            throw new IOException("Staging destination already exists: " + destination);
        owned = true;
        fileOps.CopyFile(source, destination, overwrite: false);
    }

    private static void CleanupOwnedStaging(
        VaultRestoreFileOperations fileOps, string staged, RestoreProgress progress)
    {
        var failures = new List<Exception>();
        Clean(ref progress.StagedDb, staged);
        Clean(ref progress.StagedWal, staged + "-wal");
        Clean(ref progress.StagedShm, staged + "-shm");
        if (failures.Count > 0)
            throw new AggregateException("Owned recovery staging cleanup is incomplete.", failures);

        void Clean(ref bool owned, string path)
        {
            if (!owned) return;
            try
            {
                if (fileOps.FileExists(path)) fileOps.DeleteFile(path);
                if (fileOps.FileExists(path))
                    throw new IOException("Staging artifact is still present: " + path);
                owned = false;
            }
            catch (Exception error)
            {
                failures.Add(new IOException("Could not remove owned staging artifact: " + path, error));
            }
        }
    }

    private static bool MoveIfExists(
        VaultRestoreFileOperations fileOps, string source, string destination)
    {
        if (!fileOps.FileExists(source)) return false;
        if (fileOps.FileExists(destination))
            throw new IOException("Recovery destination is already occupied; no file was replaced.");
        fileOps.MoveFile(source, destination);
        return true;
    }

    private sealed class RestoreProgress
    {
        public bool StagedDb;
        public bool StagedWal;
        public bool StagedShm;
        public bool QuarantinedDb;
        public bool QuarantinedWal;
        public bool QuarantinedShm;
        public bool PromotedDb;
        public bool PromotedWal;
        public bool PromotedShm;
    }
}
