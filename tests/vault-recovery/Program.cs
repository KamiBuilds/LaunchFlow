using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Data.Sqlite;
using System.Reflection;

var root = Path.Combine(Path.GetTempPath(), "launchflow-recovery-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("XDG_DATA_HOME", root);
Environment.SetEnvironmentVariable("LAUNCHFLOW_TEST_VAULT_PATH", Path.Combine(root, "singleton.db"));
static void Require(bool value, string message) { if (!value) throw new Exception(message); }
try
{
    Require(typeof(VaultRecoveryService).GetMethod("HasIncompletePreservation", BindingFlags.Static | BindingFlags.NonPublic) is not null,
        "Interrupted preservation has no durable startup admission marker.");
    var interruptedFolder = Path.Combine(root, "simulated-interruption"); Directory.CreateDirectory(interruptedFolder);
    Directory.CreateDirectory(Path.Combine(interruptedFolder, "recovery"));
    File.WriteAllText(Path.Combine(interruptedFolder, ".vault-preservation-pending"), "broken-fixture.db");
    File.WriteAllText(Path.Combine(interruptedFolder, "recovery", "broken-fixture.db"), "synthetic interrupted main");
    var interruptedLive = Path.Combine(interruptedFolder, "launchflow.db"); File.WriteAllText(interruptedLive + "-wal", "synthetic interrupted wal");
    var interruptedBefore = UiTests.Snapshot(interruptedFolder);
    Require(VaultRecoveryService.HasIncompletePreservation(interruptedFolder) &&
        !LaunchFlow.App.TryStartFreshVault(new VaultDatabase(interruptedLive)) &&
        UiTests.Equal(interruptedBefore, UiTests.Snapshot(interruptedFolder)), "Simulated interrupted set was treated as absent or mutated by fresh setup admission.");
    Console.WriteLine("PASS simulated_interrupted_set_marker_blocks_fresh_admission_without_mutation");
    var live = Path.Combine(root, "owned.db");
    var owned = new VaultDatabase(live);
    Require(LaunchFlow.App.TryCreateAndVerifyVault(owned, "Synthetic!Independent9", new Configuration()), "Real setup handoff refused eligible creation."); owned.Lock();
    var rejectedPath = Path.Combine(root, "rejected.db");
    Require(!LaunchFlow.App.TryCreateAndVerifyVault(new VaultDatabase(rejectedPath), "aZ7!aZ7!", new Configuration()) && !File.Exists(rejectedPath), "Pre-storage validation touched the real filesystem.");
    Console.WriteLine("PASS real_sqlite_crypto_app_handoff_and_pre_storage_validation");
    SqliteConnection.ClearAllPools(); var original = File.ReadAllBytes(live);
    Exception? refusal = null;
    try { VaultRecoveryService.QuarantineLiveVault(live, root); } catch (Exception e) { refusal = e; }
    Require(refusal is not null && File.Exists(live) && File.ReadAllBytes(live).SequenceEqual(original), "Fresh start quarantined an owned/authentication-required vault.");
    Console.WriteLine("PASS owned_authentication_boundary_preserves_bytes");
    var damaged = Path.Combine(root, "damaged-owned.db"); File.Copy(live, damaged);
    using (var connection = new SqliteConnection($"Data Source={damaged};Pooling=False"))
    {
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE secure_meta SET salt=X'01';"; command.ExecuteNonQuery();
    }
    var damagedBytes = File.ReadAllBytes(damaged);
    refusal = null; try { VaultRecoveryService.QuarantineLiveVault(damaged, root); } catch (Exception error) { refusal = error; }
    Require(refusal is not null && File.ReadAllBytes(damaged).SequenceEqual(damagedBytes), "Damaged ownership metadata was treated as permission for fresh setup.");
    Console.WriteLine("PASS damaged_ownership_metadata_requires_preservation_without_fresh_start");
    foreach (var (name, content) in new[] { ("zero", Array.Empty<byte>()), ("unknown", "synthetic unknown file"u8.ToArray()) })
    {
        var path = Path.Combine(root, name + ".db"); File.WriteAllBytes(path, content);
        File.WriteAllText(path + "-wal", "synthetic companion"); File.WriteAllText(path + "-shm", "synthetic companion");
        var quarantine = VaultRecoveryService.QuarantineLiveVault(path, root);
        Require(!File.Exists(path) && File.ReadAllBytes(quarantine).SequenceEqual(content) &&
            File.ReadAllText(quarantine + "-wal") == "synthetic companion" && File.ReadAllText(quarantine + "-shm") == "synthetic companion", "Incomplete set was not preserved together.");
    }
    Console.WriteLine("PASS explicit_unknown_and_zero_file_quarantine_preserves_entire_set");
    var previousDirectory = Environment.CurrentDirectory;
    try
    {
        Environment.CurrentDirectory = root; File.WriteAllText("relative.db", "synthetic relative");
        var relative = VaultRecoveryService.QuarantineLiveVault("relative.db", root);
        Require(File.ReadAllText(relative) == "synthetic relative" && !File.Exists("relative.db"), "Relative-path recovery did not preserve the admitted source.");
    }
    finally { Environment.CurrentDirectory = previousDirectory; }
    Console.WriteLine("PASS relative_path_snapshot_stays_owned_and_preserves_source");
    var overload = typeof(VaultRecoveryService).GetMethod("QuarantineLiveVault", [typeof(string), typeof(string), typeof(VaultRestoreFileOperations)]);
    Require(overload is not null, "Quarantine fault/ownership seam missing.");
    string Invoke(string path, VaultRestoreFileOperations ops)
    {
        try { return (string)overload!.Invoke(null, [path, root, ops])!; }
        catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
    }
    var failed = Path.Combine(root, "failed.db"); File.WriteAllText(failed, "synthetic unknown"); File.WriteAllText(failed + "-wal", "synthetic wal");
    var primary = new IOException("synthetic move refusal");
    var failureOps = new ScriptedOps { BeforeMove = (from, to) => { Require(VaultRecoveryService.HasIncompletePreservation(root), "Preservation moved files before persisting intent."); if (from.EndsWith("-wal")) throw primary; } };
    refusal = null; try { Invoke(failed, failureOps); } catch (Exception e) { refusal = e; }
    Require(ReferenceEquals(refusal, primary) && File.ReadAllText(failed) == "synthetic unknown" && File.ReadAllText(failed + "-wal") == "synthetic wal", "Companion move failure did not roll back owned moves.");
    Console.WriteLine("PASS partial_quarantine_failure_rolls_back_without_deletion");
    var collision = Path.Combine(root, "collision.db"); File.WriteAllText(collision, "synthetic original");
    string? occupied = null;
    var collisionOps = new ScriptedOps { BeforeMove = (from, to) => { if (from == collision) { occupied = to; File.WriteAllText(to, "synthetic competing winner"); } } };
    refusal = null; try { Invoke(collision, collisionOps); } catch (Exception e) { refusal = e; }
    Require(refusal is not null && File.ReadAllText(collision) == "synthetic original" && File.ReadAllText(occupied!) == "synthetic competing winner" && collisionOps.Deletes == 0, "Destination collision deleted or overwrote competing material.");
    Console.WriteLine("PASS quarantine_destination_collision_no_clobber");
    var replaced = Path.Combine(root, "replaced.db"); File.WriteAllText(replaced, "synthetic unknown");
    var replacementOps = new ScriptedOps { BeforeMove = (from, to) => { if (from == replaced) File.Copy(live, from, overwrite: true); } };
    refusal = null; try { Invoke(replaced, replacementOps); } catch (Exception e) { refusal = e; }
    Require(refusal is not null && File.ReadAllBytes(replaced).SequenceEqual(original) && replacementOps.Deletes == 0, "Authenticated replacement crossed quarantine boundary.");
    Console.WriteLine("PASS authenticated_replacement_race_refused_and_restored");
    // A competing live file blocks rollback: preserve both sets, with primary + cleanup graph.
    var blockedFolder = Path.Combine(root, "blocked-rollback"); Directory.CreateDirectory(blockedFolder);
    var blocked = Path.Combine(blockedFolder, "blocked.db"); File.WriteAllText(blocked, "synthetic old"); File.WriteAllText(blocked + "-wal", "synthetic wal");
    string? retained = null;
    var blockedOps = new ScriptedOps { BeforeMove = (from, to) =>
    {
        if (from == blocked) retained = to;
        if (from == blocked + "-wal") { File.WriteAllText(blocked, "synthetic new owner"); throw primary; }
    } };
    refusal = null; try { VaultRecoveryService.QuarantineLiveVault(blocked, blockedFolder, blockedOps); } catch (Exception e) { refusal = e; }
    Require(refusal is AggregateException aggregate && aggregate.InnerExceptions.Contains(primary) &&
        File.ReadAllText(blocked) == "synthetic new owner" && File.ReadAllText(retained!) == "synthetic old" && blockedOps.Deletes == 0,
        "Rollback failure hid primary or overwrote competing owner.");
    Require(VaultRecoveryService.HasIncompletePreservation(blockedFolder) && !VaultRecoveryService.CanStartFreshVault(blocked), "Failed rollback did not retain a fail-closed admission marker.");
    Console.WriteLine("PASS blocked_rollback_preserves_both_owners_and_primary_graph");
    foreach (var suffix in new[] { "", "-wal", "-shm" })
    {
        var folder = Path.Combine(root, "rollback-" + (suffix == "" ? "main" : suffix[1..])); Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "recovery"));
        var path = Path.Combine(folder, "launchflow.db");
        File.WriteAllText(path, "synthetic rollback main"); File.WriteAllText(path + "-wal", "synthetic rollback wal"); File.WriteAllText(path + "-shm", "synthetic rollback shm");
        var before = UiTests.Snapshot(folder);
        var operations = new ScriptedOps { BeforeMove = (from, to) => { if (from == path + suffix) throw primary; } };
        refusal = null; try { VaultRecoveryService.QuarantineLiveVault(path, folder, operations); } catch (Exception e) { refusal = e; }
        Require(ReferenceEquals(refusal, primary) && UiTests.Equal(before, UiTests.Snapshot(folder)) && operations.Deletes == 0, "Quarantine rollback changed a complete file snapshot.");
    }
    Console.WriteLine("PASS all_three_move_faults_full_snapshot_rollback_without_deletion");
    var restoreFolder = Path.Combine(root, "restore-collision"); Directory.CreateDirectory(restoreFolder);
    var restoreLive = Path.Combine(restoreFolder, "launchflow.db"); File.WriteAllText(restoreLive, "synthetic restore original");
    string? restoreWinner = null;
    var restoreOps = new ScriptedOps { BeforeExists = path =>
    {
        if (restoreWinner is null && path.Contains(Path.DirectorySeparatorChar + "recovery" + Path.DirectorySeparatorChar) && path.EndsWith(".db"))
        { restoreWinner = path; File.WriteAllText(path, "synthetic restore winner"); }
    } };
    refusal = null; try { VaultRecoveryService.RestoreSqliteBackup(live, restoreLive, restoreFolder, restoreOps); } catch (Exception e) { refusal = e; }
    Require(refusal is not null && File.ReadAllText(restoreLive) == "synthetic restore original" && File.ReadAllText(restoreWinner!) == "synthetic restore winner" && !restoreOps.DeletedPaths.Contains(restoreWinner!), "SQLite restore quarantine clobbered a competing destination.");
    Console.WriteLine("PASS sqlite_restore_quarantine_collision_preserves_both_owners");
    UiTests.Run(root, live);
    MasterAdmissionTests.Run(root);
    var log = StartupDiagnostics.FilePath; if (File.Exists(log)) File.Delete(log); Directory.CreateDirectory(log);
    var failOpen = new VaultDatabase(Path.Combine(root, "blocked-log.db"));
    Require(LaunchFlow.App.TryCreateAndVerifyVault(failOpen, "Synthetic!LogBlocked9", new Configuration()), "Blocked diagnostic sink broke real encrypted creation/cold verification.");
    failOpen.Lock(); var finalCold = new VaultDatabase(failOpen.DatabasePath);
    Require(!finalCold.Unlock("Synthetic!Other9") && finalCold.Unlock("Synthetic!LogBlocked9"), "Blocked diagnostic sink changed authentication."); finalCold.Lock();
    Console.WriteLine("PASS blocked_diagnostic_sink_real_create_cold_reopen_and_wrong_password_refusal");
    return 0;
}
catch (Exception e) { Console.Error.WriteLine("FAIL " + e.Message); return 1; }
finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }

sealed class ScriptedOps : VaultRestoreFileOperations
{
    public Action<string, string>? BeforeMove { get; init; }
    public Action<string>? BeforeExists { get; init; }
    public int Deletes { get; private set; }
    public List<string> DeletedPaths { get; } = [];
    public override bool FileExists(string path) { BeforeExists?.Invoke(path); return base.FileExists(path); }
    public override void MoveFile(string source, string destination) { BeforeMove?.Invoke(source, destination); base.MoveFile(source, destination); }
    public override void DeleteFile(string path) { Deletes++; DeletedPaths.Add(path); base.DeleteFile(path); }
}
