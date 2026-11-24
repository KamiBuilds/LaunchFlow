using LaunchFlow.Models;
using LaunchFlow.Services;

internal static partial class Program
{
    private static readonly string[] ExpectedStages =
    {
        "VaultCreateAdmission", "VaultCreateCloneConfig", "VaultCreateSerializeConfig",
        "VaultCreateRandomMaterial", "VaultCreateDeriveKey", "VaultCreateOpenConnection",
        "VaultCreateBeginTransaction", "VaultCreateEnsureSchema", "VaultCreateSealVerifier",
        "VaultCreateInsertMeta", "VaultCreateWriteConfig", "VaultCreatePersistCredentials",
        "VaultCreateIdentityToken", "VaultCreateSnapshotToken", "VaultCreateCommit",
        "VaultCreateDisposeResources", "VaultCreatePublish", "VaultCreateCleanup",
        "VaultCreatePublishCaches", "VaultCreateComplete"
    };
    private static string? Outcome(Exception error) => error.GetType().GetProperty("Outcome")?.GetValue(error)?.ToString();
    private static string? Retained(Exception error) => error.GetType().GetProperty("RetainedCreationDirectory")?.GetValue(error) as string;
    private static IEnumerable<Exception> Graph(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
            foreach (var child in aggregate.InnerExceptions)
                foreach (var item in Graph(child)) yield return item;
        else if (error.InnerException is not null)
            foreach (var item in Graph(error.InnerException)) yield return item;
    }
    private sealed class SyntheticFault : IOException;
    static partial void RunAdditionalTests()
    {
        Test("fixed ordered operation trace and completion after disposal/publication", f =>
        {
            var db = new VaultDatabase(f.Path);
            db.Initialize(Master(), new()); db.Lock();
            Check(db.CreationStages.SequenceEqual(ExpectedStages), "Missing or unordered operation checkpoints.");
        });
        foreach (var phase in ExpectedStages.TakeWhile(s => s != "VaultCreateCommit").Skip(1))
            Test("modeled precommit fault at " + phase, f =>
            {
                var db = new VaultDatabase(f.Path);
                var fault = new SyntheticFault();
                var hit = false;
                db.TestCreationOperation = op => { if (op == phase) { hit = true; throw fault; } };
                var error = Refused(() => db.Initialize(Master(), new()));
                Check(hit && ReferenceEquals(error, fault), "Fault seam was not exercised or primary replaced.");
                Check(db.CreationStages.Last() == phase, "Unwind overwrote failed operational phase.");
                Check(!File.Exists(f.Path) && !db.IsUnlocked, "Precommit failure published a vault.");
                Check(!Directory.EnumerateFileSystemEntries(f.Root).Any(), "Owned precommit resources were not removed.");
                db.TestCreationOperation = null;
                var owner = Master(); db.Initialize(owner, new() { UiLanguage = "synthetic" }); db.Lock();
                ColdOwner(f.Path, owner);
            });
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            Test("preserve orphan destination companion " + suffix, f =>
            {
                var content = new byte[] { 17, 42, 93 };
                File.WriteAllBytes(f.Path + suffix, content);
                Refused(() => new VaultDatabase(f.Path).Initialize(Master(), new()));
                Check(!File.Exists(f.Path) && File.ReadAllBytes(f.Path + suffix).SequenceEqual(content), "Orphan companion changed.");
            });
        foreach (var phase in new[] { "VaultCreateDisposeResources", "VaultCreatePublish" })
            Test("modeled postcommit fault retains complete candidate at " + phase, f =>
            {
                var db = new VaultDatabase(f.Path); var owner = Master(); var fault = new SyntheticFault(); var hit = false;
                db.TestCreationOperation = op => { if (op == phase) { hit = true; throw fault; } };
                var error = Refused(() => db.Initialize(owner, new() { UiLanguage = "synthetic" }));
                Check(hit && Graph(error).Contains(fault), "Postcommit seam not exercised or lost.");
                Check(Outcome(error) == "CandidateCommitted", "Committed candidate outcome was not explicit.");
                Check(!db.IsUnlocked && !File.Exists(f.Path), "Postcommit failure published success.");
                Check(db.CreationStages.Last() == phase, "Unwind overwrote postcommit failure.");
                var stage = Retained(error);
                Check(stage is not null && Directory.Exists(stage), "Committed candidate location was not retained.");
                ColdOwner(System.IO.Path.Combine(stage!, "candidate.db"), owner);
                Refused(() => new VaultDatabase(f.Path).Initialize(Master(), new()));
                Check(!File.Exists(f.Path), "Retry ignored retained committed candidate.");
            });
        Test("modeled commit-return/outcome-recording fault remains uncertain and retained", f =>
        {
            var db = new VaultDatabase(f.Path); var owner = Master(); var fault = new SyntheticFault(); var hit = false;
            db.TestCreationOperation = op => { if (op == "CommitReturnedBeforeOutcomeRecorded") { hit = true; throw fault; } };
            var error = Refused(() => db.Initialize(owner, new() { UiLanguage = "synthetic" }));
            Check(hit && Graph(error).Contains(fault), "Uncertain seam was not exercised.");
            Check(Outcome(error) == "CommitUncertain", "Uncertain outcome not explicit.");
            Check(!db.IsUnlocked && !File.Exists(f.Path), "Uncertain outcome published.");
            Check(db.CreationStages.Last() == "VaultCreateCommit", "Commit failure localization overwritten.");
            ColdOwner(System.IO.Path.Combine(Retained(error)!, "candidate.db"), owner);
            Refused(() => new VaultDatabase(f.Path).Initialize(Master(), new()));
        });
        Test("modeled rollback cleanup failure preserves real primary and blocks unsafe retry", f =>
        {
            var db = new VaultDatabase(f.Path); var fault = new SyntheticFault(); var hit = false;
            db.TestCreationOperation = op => { if (op == "VaultCreateCleanup") { hit = true; throw fault; } };
            var error = Refused(() => db.Initialize(Master(), new(), new() { [Guid.Empty] = new("fixture@example.invalid", "synthetic-only") }));
            Check(hit && Graph(error).Contains(fault) && Graph(error).Any(e => e is InvalidDataException), "Cleanup or real primary failure was lost.");
            Check(Outcome(error) == "NotPublished", "Failed precommit cleanup outcome not explicit.");
            Check(db.CreationStages.Last() == "VaultCreatePersistCredentials", "Cleanup concealed the failed credential writer.");
            Check(!db.IsUnlocked && !File.Exists(f.Path) && Directory.Exists(Retained(error)), "Cleanup failure state was not preserved.");
            Refused(() => new VaultDatabase(f.Path).Initialize(Master(), new()));
        });
        foreach (var phase in new[] { "VaultCreateCleanup", "VaultCreatePublishCaches" })
            Test("modeled published failure never removes owner at " + phase, f =>
            {
                var db = new VaultDatabase(f.Path); var owner = Master(); var fault = new SyntheticFault(); var hit = false;
                db.TestCreationOperation = op => { if (op == phase) { hit = true; throw fault; } };
                var error = Refused(() => db.Initialize(owner, new() { UiLanguage = "synthetic" }));
                Check(hit && Graph(error).Contains(fault), "Published seam was not exercised.");
                Check(Outcome(error) == "Published", "Published outcome not explicit.");
                Check(!db.IsUnlocked && File.Exists(f.Path), "Published failure lost owner or reported session.");
                Check(db.CreationStages.Last() == phase, "Published failure trace overwritten.");
                ColdOwner(f.Path, owner);
            });
        Test("replacement destination arriving after admission is preserved", f =>
        {
            var db = new VaultDatabase(f.Path); var foreign = new byte[] { 91, 28, 37 }; var hit = false;
            db.TestCreationOperation = op => { if (op == "VaultCreatePublish") { File.WriteAllBytes(f.Path, foreign); hit = true; } };
            Refused(() => db.Initialize(Master(), new()));
            Check(hit && File.ReadAllBytes(f.Path).SequenceEqual(foreign), "Concurrent replacement was touched.");
            Check(!db.IsUnlocked, "Concurrent replacement was adopted.");
        });
        Test("retained interrupted stage is classified for non-destructive recovery", f =>
        {
            var stage = System.IO.Path.Combine(f.Root, ".vault.db.create-synthetic-interrupted");
            Directory.CreateDirectory(stage);
            var file = System.IO.Path.Combine(stage, "candidate.db");
            var bytes = new byte[] { 17, 22, 93 }; File.WriteAllBytes(file, bytes);
            var db = new VaultDatabase(f.Path);
            Check(db.InspectStorage() == VaultStorageState.IncompleteOrUnsupported, "Retained stage was presented as absent.");
            Check(!db.Unlock(Master()), "Unknown stage was adopted.");
            Refused(() => db.Initialize(Master(), new()));
            Check(File.ReadAllBytes(file).SequenceEqual(bytes) && !File.Exists(f.Path), "Retained material changed.");
        });
        Test("directory at destination is incomplete rather than fresh setup", f =>
        {
            Directory.CreateDirectory(f.Path);
            Check(new VaultDatabase(f.Path).InspectStorage() == VaultStorageState.IncompleteOrUnsupported, "Destination directory presented as absent.");
        });
        Test("modeled publication-return fault is explicit and never deletes either location", f =>
        {
            var db = new VaultDatabase(f.Path); var owner = Master(); var fault = new SyntheticFault(); var hit = false;
            db.TestCreationOperation = op => { if (op == "PublishReturnedBeforeOutcomeRecorded") { hit = true; throw fault; } };
            var error = Refused(() => db.Initialize(owner, new() { UiLanguage = "synthetic" }));
            Check(hit && Graph(error).Contains(fault) && Outcome(error) == "PublicationUncertain", "Publication uncertainty was not retained.");
            Check(!db.IsUnlocked && File.Exists(f.Path), "Uncertain publication lost the owner or reported success.");
            ColdOwner(f.Path, owner);
        });
        foreach (var iteration in Enumerable.Range(1, 12))
        Test("two independently admitted creators cannot overwrite the winner #" + iteration, f =>
        {
            using var admitted = new Barrier(2); using var publish = new Barrier(2);
            var a = new VaultDatabase(f.Path); var b = new VaultDatabase(f.Path);
            var ownerA = Master(); var ownerB = Master();
            var admissionHits = 0; var publishHits = 0;
            Action<string> sync = op =>
            {
                if (op == "VaultCreateCloneConfig") { Interlocked.Increment(ref admissionHits); Check(admitted.SignalAndWait(TimeSpan.FromSeconds(30)), "Admission barrier timed out."); }
                if (op == "PublishTargetCheckedBeforeMove") { Interlocked.Increment(ref publishHits); Check(publish.SignalAndWait(TimeSpan.FromSeconds(30)), "Publish barrier timed out."); }
            };
            a.TestCreationOperation = sync; b.TestCreationOperation = sync;
            Exception? failureA = null, failureB = null;
            var first = Task.Run(() => { try { a.Initialize(ownerA, new() { UiLanguage = "synthetic" }); } catch (Exception e) { failureA = e; } });
            var second = Task.Run(() => { try { b.Initialize(ownerB, new() { UiLanguage = "synthetic" }); } catch (Exception e) { failureB = e; } });
            Check(Task.WaitAll(new[] { first, second }, TimeSpan.FromSeconds(90)), "Creation tasks timed out.");
            Check(admissionHits == 2 && publishHits == 2, "Concurrent creators did not both reach real admission/publication barriers.");
            Check((failureA is null) != (failureB is null), "Creation did not select exactly one winner: A=" + (failureA?.GetType().Name ?? "success") + "/" + (failureA is null ? "" : Outcome(failureA)) + ";B=" + (failureB?.GetType().Name ?? "success") + "/" + (failureB is null ? "" : Outcome(failureB)));
            var winning = failureA is null ? ownerA : ownerB;
            var losing = failureA is null ? ownerB : ownerA;
            Check((failureA is null ? a : b).IsUnlocked && !(failureA is null ? b : a).IsUnlocked, "Loser published a key.");
            a.Lock(); b.Lock(); ColdOwner(f.Path, winning);
            var cold = new VaultDatabase(f.Path);
            try { Check(!cold.Unlock(losing), "Losing creator overwrote the winner."); }
            finally { cold.Lock(); }
        });
        RunChallengeTests();
    }
}
