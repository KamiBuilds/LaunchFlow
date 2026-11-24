using System.Reflection;
using System.Security.Cryptography;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Data.Sqlite;

internal static partial class Program
{
    private static void RunStorageCharacterizations()
    {
        foreach (var kind in new[] { "empty-sqlite", "schema-no-owner", "foreign-sqlite", "nonempty-lease", "unknown-stage-file", "nested-stage", "parent-is-file" })
            Test("read-only incomplete classification and preservation: " + kind, f =>
            {
                var target = f.Path;
                if (kind is "empty-sqlite" or "schema-no-owner" or "foreign-sqlite")
                {
                    using var c = new SqliteConnection("Data Source=" + target + ";Pooling=False"); c.Open();
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = kind == "empty-sqlite" ? "PRAGMA user_version=0;" :
                        kind == "schema-no-owner" ? "CREATE TABLE secure_meta(id INTEGER);" : "CREATE TABLE unrelated(value TEXT); INSERT INTO unrelated VALUES('synthetic');";
                    cmd.ExecuteNonQuery();
                }
                else if (kind is "nonempty-lease" or "unknown-stage-file")
                    File.WriteAllBytes(System.IO.Path.Combine(f.Root, ".vault.db.create-foreign" + (kind == "nonempty-lease" ? ".lease" : "")), new byte[] { 19, 25, 31 });
                else if (kind == "nested-stage") Directory.CreateDirectory(System.IO.Path.Combine(f.Root, ".vault.db.create-foreign", "nested"));
                else { File.WriteAllBytes(f.Path, new byte[] { 11 }); target = System.IO.Path.Combine(f.Path, "vault.db"); }
                var before = SnapshotFiles(f.Root);
                var db = new VaultDatabase(target);
                Check(db.InspectStorage() == VaultStorageState.IncompleteOrUnsupported, "Invalid or obstructed path was classified as fresh.");
                Refused(() => db.Initialize(Master(), new()));
                Check(SameFiles(before, SnapshotFiles(f.Root)), "Classification/admission changed retained bytes.");
            });

        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            Test("late foreign target survives precommit rollback: " + (suffix == "" ? "main" : suffix), f =>
            {
                var bytes = RandomNumberGenerator.GetBytes(53); var hit = false;
                var db = new VaultDatabase(f.Path);
                db.TestCreationOperation = op => { if (op == "VaultCreatePersistCredentials") { hit = true; File.WriteAllBytes(f.Path + suffix, bytes); } };
                var failure = Refused(() => db.Initialize(Master(), new(), new() { [Guid.Empty] = new("fixture@example.invalid", "synthetic") }));
                Check(hit && failure is InvalidDataException, "Real rollback did not execute.");
                Check(File.ReadAllBytes(f.Path + suffix).SequenceEqual(bytes), "Rollback touched a foreign destination.");
                Check(!db.IsUnlocked, "Failed operation published key material.");
            });

        Test("late destination directory after final check is not clobbered", f =>
        {
            var db = new VaultDatabase(f.Path); var hit = false;
            db.TestCreationOperation = op => { if (op == "PublishTargetCheckedBeforeMove") { hit = true; Directory.CreateDirectory(f.Path); File.WriteAllBytes(System.IO.Path.Combine(f.Path, "foreign"), new byte[] { 37 }); } };
            var failure = Refused(() => db.Initialize(Master(), new()));
            Check(hit && Directory.Exists(f.Path) && File.ReadAllBytes(System.IO.Path.Combine(f.Path, "foreign")).SequenceEqual(new byte[] { 37 }), "Publication clobbered a late directory.");
            Check(!db.IsUnlocked && Outcome(failure) == "PublicationUncertain", "Failed publication reported a session.");
        });

        Test("real WAL reader prevents unsafe main-only publication and retains cold-reopenable candidate", f =>
        {
            var db = new VaultDatabase(f.Path); var owner = Master();
            SqliteConnection? reader = null; SqliteTransaction? read = null; string? candidate = null;
            try
            {
                db.TestCreationOperation = op =>
                {
                    if (op != "CommitReturnedBeforeOutcomeRecorded") return;
                    candidate = Directory.GetFiles(f.Root, "candidate.db", SearchOption.AllDirectories).Single();
                    reader = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = candidate, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                    reader.Open(); read = reader.BeginTransaction();
                    using var cmd = reader.CreateCommand(); cmd.Transaction = read;
                    cmd.CommandText = "SELECT sealed FROM secure_objects WHERE id='config';";
                    Check(cmd.ExecuteScalar() is byte[], "Reader did not pin actual committed config.");
                };
                var failure = Refused(() => db.Initialize(owner, new() { UiLanguage = "synthetic" }));
                Check(candidate is not null && Outcome(failure) == "CandidateCommitted", "Checkpoint refusal did not report retained committed data.");
                Check(!File.Exists(f.Path) && !db.IsUnlocked && Directory.Exists(Retained(failure)), "Busy checkpoint published or lost candidate.");
            }
            finally { read?.Dispose(); reader?.Dispose(); }
            ColdOwner(candidate!, owner);
        });

        foreach (var failed in new[] { false, true })
            Test("creation candidate key, serialization and tokens are cleared after " + (failed ? "failure" : "lock"), f =>
            {
                var db = new VaultDatabase(f.Path); var observed = new List<byte[]>();
                db.TestCreationOperation = op =>
                {
                    if (op != "VaultCreatePublishCaches") return;
                    if (failed) throw new SyntheticFault();
                };
                // Operation-local key buffers are observed only by an erased test hook.
                VaultDatabase.TestSensitiveBuffer = (kind, bytes) => { if (kind.StartsWith("Creation", StringComparison.Ordinal)) observed.Add(bytes); };
                try
                {
                    if (failed) Refused(() => db.Initialize(Master(), new()));
                    else { db.Initialize(Master(), new()); db.Lock(); }
                    Check(observed.Count == 4 && observed.All(bytes => bytes.All(b => b == 0)), "Creation did not zero its owned material.");
                    foreach (var field in new[] { "_key", "_strictIdentityToken", "_strictSnapshotToken", "_configCache", "_secretsCache" })
                        Check(typeof(VaultDatabase).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(db) is null, "Failed or locked creation retained live authority.");
                }
                finally { VaultDatabase.TestSensitiveBuffer = null; db.Lock(); }
            });
    }

    private static Dictionary<string, byte[]> SnapshotFiles(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(p => System.IO.Path.GetRelativePath(root, p), File.ReadAllBytes);
    private static bool SameFiles(Dictionary<string, byte[]> a, Dictionary<string, byte[]> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var bytes) && pair.Value.SequenceEqual(bytes));
}
