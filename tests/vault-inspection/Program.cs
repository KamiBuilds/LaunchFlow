using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Data.Sqlite;

internal sealed class WholeLiveRootChangedException : Exception { }
internal sealed class MissingInspectionHelperException : Exception { }

internal static class Program
{
    private const string Owner = "Synthetic!Inspection-Owner9";
    private static int failures, total;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "lf-inspect-" + Guid.NewGuid().ToString("N"));
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--hot-writer") return HotWriter(args[1]);
        Directory.CreateDirectory(Root);
        try
        {
            Test("cold_WAL_inspection_preserves_whole_live_root", ColdWal);
            Test("empty_journal_preserves_healthy_owner_admission", () => HealthyJournal("empty"));
            Test("committed_PERSIST_journal_preserves_healthy_owner_admission", () => HealthyJournal("persist"));
            Test("genuine_hot_journal_requires_owned_recovery_and_preserves_live_set", HotJournal);
            Test("absent_root_stays_absent", () => PreservedInspection(Path.Combine(Root, "absent", "vault.db"), VaultStorageState.Absent));
            Test("orphaned_companions_are_retained", Orphans);
            Test("retained_and_inert_creation_stages_keep_classification", Stages);
            Test("committed_WAL_is_required_and_existing_set_is_preserved", RequiredWal);
            Test("corrupt_and_incomplete_files_are_retained", Corrupt);
            Test("legacy_v1_and_v2_are_structural_only", Legacy);
            Test("directories_and_journal_refuse_without_mutation", Obstructions);
            Test("reparse_main_companions_and_parent_refuse", Links);
            Test("observable_postcopy_mutations_are_refused_and_owned_temp_removed", ChangedCopy);
            Test("copy_failure_is_closed_and_owned_temp_removed", CopyFailure);
            Console.WriteLine($"RESULT cases={total} failures={failures}");
            return failures == 0 ? 0 : 1;
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
    }
    private static void Test(string name, Action action)
    {
        total++;
        try { action(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + " type=" + ex.GetType().Name); }
    }
    internal static void Require(bool value) { if (!value) throw new InvalidOperationException("Synthetic inspection assertion failed."); }
    private static string Fixture(string name)
    {
        var dir = Path.Combine(Root, name); Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "unrelated"));
        File.WriteAllText(Path.Combine(dir, "unrelated", "sentinel"), "synthetic unrelated bytes");
        return Path.Combine(dir, "vault.db");
    }
    private static byte[] Read(string p)
    {
        using var input = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream(); input.CopyTo(bytes); return bytes.ToArray();
    }
    private static Dictionary<string, byte[]> Snapshot(string root) => Directory.Exists(root)
        ? Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).ToDictionary(
            p => Path.GetRelativePath(root, p) + (Directory.Exists(p) ? "/" : ""),
            p => Directory.Exists(p) ? Array.Empty<byte>() : Read(p), StringComparer.Ordinal)
        : new();
    private static bool Equal(Dictionary<string, byte[]> a, Dictionary<string, byte[]> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var value) && p.Value.SequenceEqual(value));
    private static void PreservedInspection(string path, VaultStorageState expected)
    {
        var dir = Path.GetDirectoryName(path)!; var existed = Directory.Exists(dir); var before = Snapshot(dir);
        var tempBefore = Directory.GetDirectories(Path.GetTempPath(), "LaunchFlow-VaultInspection-*").Order().ToArray();
        var db = new VaultDatabase(path); var session = db.SessionGeneration;
        var state = db.InspectStorage();
        if (Directory.Exists(dir) != existed || !Equal(before, Snapshot(dir))) throw new WholeLiveRootChangedException();
        Require(state == expected && !db.IsUnlocked && db.SessionGeneration == session);
        Require(tempBefore.SequenceEqual(Directory.GetDirectories(Path.GetTempPath(), "LaunchFlow-VaultInspection-*").Order()));
    }
    private static void Orphans()
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var path = Fixture("orphan" + suffix); File.WriteAllText(path + suffix, "synthetic retained companion");
            PreservedInspection(path, VaultStorageState.IncompleteOrUnsupported);
        }
    }
    private static void Stages()
    {
        var path = Fixture("stage"); var stage = Path.Combine(Path.GetDirectoryName(path)!, ".vault.db.create-fixture");
        Directory.CreateDirectory(stage); PreservedInspection(path, VaultStorageState.Absent);
        File.WriteAllText(Path.Combine(stage, "candidate.db"), "synthetic retained stage");
        PreservedInspection(path, VaultStorageState.IncompleteOrUnsupported);
    }
    private static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open(); return c;
    }
    private static void Sql(SqliteConnection c, string text)
    {
        using var command = c.CreateCommand(); command.CommandText = text; command.ExecuteNonQuery();
    }
    private static void RequiredWal()
    {
        var path = Fixture("required-wal"); var db = new VaultDatabase(path); db.Initialize(Owner, new Configuration()); db.Lock();
        using (var writer = Open(path))
        {
            Sql(writer, "PRAGMA wal_autocheckpoint=0;");
            byte[] sealedConfig;
            using (var q = writer.CreateCommand()) { q.CommandText = "SELECT sealed FROM secure_objects WHERE id='config';"; sealedConfig = (byte[])q.ExecuteScalar()!; }
            Sql(writer, "DELETE FROM secure_objects WHERE id='config'; PRAGMA wal_checkpoint(TRUNCATE);");
            using (var q = writer.CreateCommand())
            {
                q.CommandText = "INSERT INTO secure_objects(id,sealed) VALUES('config',$s);";
                q.Parameters.AddWithValue("$s", sealedConfig); q.ExecuteNonQuery();
            }
            Require(File.Exists(path + "-shm") && new FileInfo(path + "-wal").Length > 0);
            var control = Fixture("main-only"); File.WriteAllBytes(control, Read(path));
            PreservedInspection(control, VaultStorageState.IncompleteOrUnsupported);
            PreservedInspection(path, VaultStorageState.PresentNeedsAuthentication);
            PreservedInspection(path, VaultStorageState.PresentNeedsAuthentication);
        }
        var cold = new VaultDatabase(path); Require(cold.Unlock(Owner)); cold.Lock();
    }
    private static void Corrupt()
    {
        foreach (var content in new[] { Array.Empty<byte>(), new byte[] { 1, 2, 3, 4 } })
        {
            var path = Fixture("corrupt-" + content.Length); File.WriteAllBytes(path, content);
            PreservedInspection(path, VaultStorageState.IncompleteOrUnsupported);
        }
        var incomplete = Fixture("metadata-only");
        using (var c = Open(incomplete)) Sql(c, "CREATE TABLE secure_meta(id INTEGER);");
        PreservedInspection(incomplete, VaultStorageState.IncompleteOrUnsupported);
    }
    private static void Legacy()
    {
        foreach (var schema in new[] { 1, 2 })
        {
            var path = Fixture("legacy-" + schema);
            using (var c = Open(path))
            {
                Sql(c, "PRAGMA journal_mode=WAL; CREATE TABLE vault_meta(id INTEGER,salt BLOB,verifier BLOB,argon_memory_kb INTEGER,argon_iterations INTEGER,argon_lanes INTEGER,schema_version INTEGER);" +
                    "CREATE TABLE objects(id TEXT,sealed BLOB); INSERT INTO vault_meta VALUES(1,zeroblob(16),zeroblob(28),16384,1,1," + schema + ");" +
                    "INSERT INTO objects VALUES('config',zeroblob(28));" + (schema == 2 ? "CREATE TABLE credentials(workflow_id TEXT,sealed BLOB);" : ""));
            }
            PreservedInspection(path, VaultStorageState.LegacyUpgradeRequired);
            Require(!new VaultDatabase(path).Unlock(Owner));
            using (var writer = Open(path))
                Sql(writer, "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE; PRAGMA journal_mode=PERSIST; " +
                    "BEGIN IMMEDIATE; CREATE TABLE legacy_journal_control(value INTEGER); INSERT INTO legacy_journal_control VALUES(1); COMMIT;");
            Require(File.Exists(path + "-journal") && Read(path + "-journal").Length > 512);
            PreservedInspection(path, VaultStorageState.LegacyUpgradeRequired);
        }
    }
    private static void Obstructions()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var path = Fixture("dir" + suffix);
            if (suffix != "") { var db = new VaultDatabase(path); db.Initialize(Owner, new Configuration()); db.Lock(); }
            Directory.CreateDirectory(path + suffix); PreservedInspection(path, VaultStorageState.IncompleteOrUnsupported);
        }
        var journalPath = Fixture("journal"); var vault = new VaultDatabase(journalPath); vault.Initialize(Owner, new Configuration()); vault.Lock();
        // Unknown nonzero journal bytes are a refusal control, not a genuine hot-journal fixture.
        File.WriteAllText(journalPath + "-journal", "synthetic unknown journal bytes");
        PreservedInspection(journalPath, VaultStorageState.IncompleteOrUnsupported);
    }
    private static void Links()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var path = Fixture("link" + suffix); var target = Path.Combine(Path.GetDirectoryName(path)!, "target"); File.WriteAllText(target, "synthetic linked file");
            if (suffix != "") { var db = new VaultDatabase(path); db.Initialize(Owner, new Configuration()); db.Lock(); }
            File.CreateSymbolicLink(path + suffix, target); PreservedInspection(path, VaultStorageState.IncompleteOrUnsupported);
        }
        var actual = Fixture("actual-parent"); var owned = new VaultDatabase(actual); owned.Initialize(Owner, new Configuration()); owned.Lock();
        var linked = Path.Combine(Root, "linked-parent"); Directory.CreateSymbolicLink(linked, Path.GetDirectoryName(actual)!);
        PreservedInspection(Path.Combine(linked, "vault.db"), VaultStorageState.IncompleteOrUnsupported);
        PreservedInspection(Path.Combine(linked, "absent.db"), VaultStorageState.IncompleteOrUnsupported);
    }
    private static void ChangedCopy()
    {
        var type = typeof(VaultDatabase).GetNestedType("InspectionSnapshot", System.Reflection.BindingFlags.NonPublic);
        if (type is null) throw new MissingInspectionHelperException();
        foreach (var mode in new[] { "main-content", "added-wal", "removed-wal", "wal-content", "added-journal", "removed-journal", "journal-content" })
        {
            var path = Fixture("race-" + mode); var vault = new VaultDatabase(path); vault.Initialize(Owner, new Configuration()); vault.Lock();
            if (mode is "removed-wal" or "wal-content") File.WriteAllBytes(path + "-wal", new byte[32]);
            if (mode is "removed-journal" or "journal-content") File.WriteAllBytes(path + "-journal", new byte[32]);
            var beforeTemp = Directory.GetDirectories(Path.GetTempPath(), "LaunchFlow-VaultInspection-*").Order().ToArray();
            var blockedDeletion = false;
            using (var snapshot = (IDisposable)Activator.CreateInstance(type, path)!)
            {
                var suffix = mode.EndsWith("journal", StringComparison.Ordinal) || mode == "journal-content" ? "-journal" : "-wal";
                if (mode is "main-content" or "wal-content" or "journal-content")
                {
                    var target = path + (mode == "main-content" ? "" : suffix); var stamp = File.GetLastWriteTimeUtc(target);
                    var bytes = Read(target); bytes[^1] ^= 1; File.WriteAllBytes(target, bytes); File.SetLastWriteTimeUtc(target, stamp);
                }
                else if (mode is "added-wal" or "added-journal") File.WriteAllBytes(path + suffix, new byte[32]);
                else
                {
                    try { File.Delete(path + suffix); }
                    catch (IOException) when (OperatingSystem.IsWindows())
                    {
                        // The source read handle denies deletion on Windows.
                        Require(File.Exists(path + suffix));
                        type.GetMethod("VerifyUnchanged")!.Invoke(snapshot, null);
                        blockedDeletion = true;
                    }
                }
                var injected = Snapshot(Path.GetDirectoryName(path)!); var refused = false;
                try { type.GetMethod("VerifyUnchanged")!.Invoke(snapshot, null); }
                catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is IOException) { refused = true; }
                Require(refused == !blockedDeletion && Equal(injected, Snapshot(Path.GetDirectoryName(path)!)));
            }
            Require(beforeTemp.SequenceEqual(Directory.GetDirectories(Path.GetTempPath(), "LaunchFlow-VaultInspection-*").Order()));
        }
    }
    private static void CopyFailure()
    {
        var path = Fixture("sharing-refusal"); var healthy = new VaultDatabase(path); healthy.Initialize(Owner, new Configuration()); healthy.Lock();
        PreservedInspection(path, VaultStorageState.PresentNeedsAuthentication);
        var files = Snapshot(Path.GetDirectoryName(path)!);
        var before = Directory.GetDirectories(Path.GetTempPath(), "LaunchFlow-VaultInspection-*").Order().ToArray();
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Require(new VaultDatabase(path).InspectStorage() == VaultStorageState.IncompleteOrUnsupported);
        Require(Equal(files, Snapshot(Path.GetDirectoryName(path)!)) &&
            before.SequenceEqual(Directory.GetDirectories(Path.GetTempPath(), "LaunchFlow-VaultInspection-*").Order()));
        PreservedInspection(path, VaultStorageState.PresentNeedsAuthentication);
        Require(Equal(files, Snapshot(Path.GetDirectoryName(path)!)));
        var cold = new VaultDatabase(path); Require(cold.Unlock(Owner)); cold.Lock();
    }
    private static void HealthyJournal(string mode)
    {
        var path = Fixture("healthy-journal-" + mode); var db = new VaultDatabase(path);
        db.Initialize(Owner, new Configuration()); db.Lock();
        if (mode == "empty") File.WriteAllBytes(path + "-journal", Array.Empty<byte>());
        else
        {
            using var writer = Open(path);
            Sql(writer, "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE; PRAGMA journal_mode=PERSIST; " +
                "BEGIN IMMEDIATE; CREATE TABLE journal_control(value INTEGER); INSERT INTO journal_control VALUES(1); COMMIT;");
        }
        Require(File.Exists(path + "-journal"));
        var journal = Read(path + "-journal");
        Require(mode == "empty" ? journal.Length == 0 : journal.Length > 512 && journal.Take(8).All(b => b == 0));
        var before = Snapshot(Path.GetDirectoryName(path)!);
        Console.WriteLine("CONTROL journal_" + mode + "_structural");
        PreservedInspection(path, VaultStorageState.PresentNeedsAuthentication);
        Require(Equal(before, Snapshot(Path.GetDirectoryName(path)!)));
        Console.WriteLine("CONTROL journal_" + mode + "_unlock");
        var cold = new VaultDatabase(path); Require(cold.Unlock(Owner));
        SqliteConnection.ClearAllPools();
        // Authorized unlock can legitimately change SQLite journal mode/companions.
        // Confirmation must still admit a genuine committed PERSIST companion.
        Console.WriteLine("CONTROL journal_" + mode + "_confirm_fixture");
        if (mode == "empty") File.WriteAllBytes(path + "-journal", Array.Empty<byte>());
        else
        {
            using var writer = Open(path);
            Sql(writer, "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE; PRAGMA journal_mode=PERSIST; " +
                "BEGIN IMMEDIATE; CREATE TABLE confirm_control(value INTEGER); INSERT INTO confirm_control VALUES(1); COMMIT;");
        }
        Require(File.Exists(path + "-journal"));
        PreservedInspection(path, VaultStorageState.PresentNeedsAuthentication);
        before = Snapshot(Path.GetDirectoryName(path)!);
        Console.WriteLine("CONTROL journal_" + mode + "_confirm");
        var session = cold.SessionGeneration;
        Require(cold.ConfirmPassword(Owner) && cold.SessionGeneration == session); cold.Lock();
        // Byte-preservation is asserted at structural inspection, not at an
        // authorized OpenConnection that intentionally switches journal modes.
        Require(before.ContainsKey("vault.db-journal"));
        var reopen = new VaultDatabase(path); Require(reopen.Unlock(Owner)); reopen.Lock();
    }
    private static int HotWriter(string path)
    {
        try
        {
            using var writer = Open(path);
            Sql(writer, "PRAGMA journal_mode=DELETE; PRAGMA cache_size=8; PRAGMA cache_spill=ON; " +
                "BEGIN IMMEDIATE; UPDATE hot_padding SET payload=randomblob(4096);");
            File.WriteAllText(path + ".ready", "synthetic transaction reached");
            System.Threading.Thread.Sleep(60000);
            return 3;
        }
        catch { return 2; }
    }
    private static void HotJournal()
    {
        var path = Fixture("hot-journal"); var vault = new VaultDatabase(path); vault.Initialize(Owner, new Configuration()); vault.Lock();
        using (var writer = Open(path))
            Sql(writer, "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE; CREATE TABLE hot_padding(id INTEGER PRIMARY KEY,payload BLOB); " +
                "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<128) INSERT INTO hot_padding SELECT x,zeroblob(4096) FROM n;");
        var cleanMain = Read(path);
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--hot-writer"); start.ArgumentList.Add(path);
        using (var child = System.Diagnostics.Process.Start(start)!)
        {
            try
            {
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                while (!File.Exists(path + ".ready") && !child.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(15))
                    System.Threading.Thread.Sleep(20);
                Require(File.Exists(path + ".ready") && !child.HasExited);
            }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                Require(child.WaitForExit(10000));
            }
        }
        var journal = Read(path + "-journal");
        Require(journal.Length > 512 && journal.Take(8).SequenceEqual(new byte[] { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 }));
        Require(!cleanMain.SequenceEqual(Read(path)));
        var original = Snapshot(Path.GetDirectoryName(path)!);
        PreservedInspection(path, VaultStorageState.IncompleteOrUnsupported);
        Require(!new VaultDatabase(path).Unlock(Owner) && Equal(original, Snapshot(Path.GetDirectoryName(path)!)));
        // A writable independently owned clone must actually replay the rollback
        // journal and recover original padding; never open the source with SQLite.
        var recovered = Fixture("hot-recovered"); File.WriteAllBytes(recovered, Read(path)); File.WriteAllBytes(recovered + "-journal", journal);
        using (var writer = Open(recovered))
        {
            using var query = writer.CreateCommand(); query.CommandText = "SELECT count(*) FROM hot_padding WHERE payload=zeroblob(4096);";
            Require(Convert.ToInt32(query.ExecuteScalar()) == 128);
        }
        PreservedInspection(recovered, VaultStorageState.PresentNeedsAuthentication);
        var cold = new VaultDatabase(recovered); Require(cold.Unlock(Owner)); cold.Lock();
        Require(Equal(original, Snapshot(Path.GetDirectoryName(path)!)));
    }
    private static void ColdWal()
    {
        var path = Fixture("cold"); var db = new VaultDatabase(path);
        db.Initialize(Owner, new Configuration()); db.Lock(); SqliteConnection.ClearAllPools();
        Require(!File.Exists(path + "-wal") && !File.Exists(path + "-shm"));
        Require(Read(path)[18] == 2);
        PreservedInspection(path, VaultStorageState.PresentNeedsAuthentication);
        var cold = new VaultDatabase(path); Require(cold.Unlock(Owner)); cold.Lock();
    }
}
