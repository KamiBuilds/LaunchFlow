using System.Security.Cryptography;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Data.Sqlite;

internal static partial class Program
{
    private static int _total, _failures;
    private static string Master() => "Aa!9-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private static void Require(bool value, string fixedMessage)
    { if (!value) throw new InvalidOperationException(fixedMessage); }
    private static Exception Refused(Action action)
    {
        try { action(); }
        catch (Exception e) { return e; }
        throw new InvalidOperationException("Expected refusal.");
    }
    private static void Test(string name, Action<Fixture> body)
    {
        _total++;
        try { using var fixture = new Fixture(); body(fixture); Console.WriteLine("PASS " + name); }
        catch (Exception e) { _failures++; Console.WriteLine("FAIL " + name + " [" + e.GetType().Name + "]: " + (e is AssertionFailure ? e.Message : "Unexpected or failed requirement; no exception payload retained.")); }
    }
    private static void Check(bool value, string message)
    { if (!value) throw new AssertionFailure(message); }
    private static void ColdOwner(string path, string master)
    {
        var cold = new VaultDatabase(path);
        try
        {
            Check(!cold.Unlock(Master()), "Unrelated master accepted.");
            Check(cold.Unlock(master), "Owner cold unlock failed.");
            Check(cold.LoadConfiguration().UiLanguage == "synthetic", "Cold config changed.");
        }
        finally { cold.Lock(); }
    }
    private static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--permissions-only" }))
        {
            RunPermissionTests();
            Console.WriteLine($"RESULT total={_total} passed={_total - _failures} failed={_failures}");
            return _failures == 0 ? 0 : 1;
        }
        Test("healthy creation and cold owner / wrong refusal", f =>
        {
            var master = Master();
            var db = new VaultDatabase(f.Path);
            db.Initialize(master, new() { UiLanguage = "synthetic" });
            Check(db.IsUnlocked, "Creation did not publish a session.");
            db.Lock(); ColdOwner(f.Path, master);
        });
        Test("real Guid.Empty precommit rollback leaves destination absent and ordinary retry succeeds", f =>
        {
            var master = Master();
            var db = new VaultDatabase(f.Path);
            var error = Refused(() => db.Initialize(master, new(),
                new() { [Guid.Empty] = new("fixture@example.invalid", "synthetic-only") }));
            Check(error is InvalidDataException, "The actual credential writer did not reject.");
            Check(!db.IsUnlocked, "Failed creation published key.");
            Check(!File.Exists(f.Path), "Rolled-back creation left destination residue.");
            db.Initialize(master, new() { UiLanguage = "synthetic" });
            db.Lock(); ColdOwner(f.Path, master);
        });
        foreach (var kind in new[] { "zero", "foreign", "owned", "directory" })
            Test("preserve preexisting " + kind, f =>
            {
                string? owner = null;
                if (kind == "zero") File.WriteAllBytes(f.Path, Array.Empty<byte>());
                if (kind == "foreign") File.WriteAllBytes(f.Path, RandomNumberGenerator.GetBytes(49));
                if (kind == "directory") Directory.CreateDirectory(f.Path);
                if (kind == "owned")
                {
                    owner = Master(); var original = new VaultDatabase(f.Path);
                    original.Initialize(owner, new() { UiLanguage = "synthetic" }); original.Lock();
                }
                var before = kind == "directory" ? null : File.ReadAllBytes(f.Path);
                var db = new VaultDatabase(f.Path);
                Refused(() => db.Initialize(Master(), new()));
                Check(!db.IsUnlocked, "Refusal published session.");
                if (before is not null) Check(before.SequenceEqual(File.ReadAllBytes(f.Path)), "Existing bytes changed.");
                else Check(Directory.Exists(f.Path), "Existing directory removed.");
                if (owner is not null) ColdOwner(f.Path, owner);
            });
        RunAdditionalTests();
        Console.WriteLine($"RESULT total={_total} passed={_total - _failures} failed={_failures}");
        return _failures == 0 ? 0 : 1;
    }
    static partial void RunAdditionalTests();
    private sealed class AssertionFailure(string message) : Exception(message);
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lf-create-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public string Root => _root;
        public Fixture() { Directory.CreateDirectory(_root); Path = System.IO.Path.Combine(_root, "vault.db"); }
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
    }
}
