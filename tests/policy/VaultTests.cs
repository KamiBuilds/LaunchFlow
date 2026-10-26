using System.Security.Cryptography;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Data.Sqlite;

internal static class VaultTests
{
    private const string Master = "FixtureOnly-Master-38!";
    internal static void Run(Action<string, Action> test)
    {
        var root = Environment.GetEnvironmentVariable("LAUNCHFLOW_POLICY_TEST_ROOT")
            ?? throw new InvalidOperationException("Use tests/policy/run.sh for isolated storage.");
        var db = VaultDatabase.Current;
        Check(Path.GetFullPath(db.DatabasePath).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Check(!db.Exists);
        var id = Guid.NewGuid();
        var credential = new SiteCredential("FixtureUser42", "FixtureOnlySecret99");
        var config = new Configuration { Workflows = new() { new WebsiteWorkflow { Id = id, Name = "Fixture workflow", Enabled = true } } };
        var secrets = new Dictionary<Guid, SiteCredential> { [id] = credential };
        var vault = new CredentialVault();

        test("Initialize with credentials refuses policy before creating a database", () =>
        {
            try
            {
                using (Policy(("DisableCredentialStorage", 1))) Refused(() => db.Initialize(Master, config, secrets));
                Check(!db.Exists);
            }
            finally
            {
                db.Lock();
                SqliteConnection.ClearAllPools();
                foreach (var file in Directory.Exists(db.DataDirectory) ? Directory.GetFiles(db.DataDirectory) : Array.Empty<string>()) File.Delete(file);
            }
        });
        db.Initialize(Master, config, secrets);

        void Boundary(string name, Action action, params (string Name, object? Value)[] policy)
        {
            test(name, () =>
            {
                try
                {
                    var before = Fingerprint(db.DataDirectory);
                    using (Policy(policy)) Refused(action);
                    Check(before == Fingerprint(db.DataDirectory));
                    Check(db.ReadCredential(id) == credential);
                }
                finally { db.ReplaceSnapshot(config, secrets); }
            });
        }
        Boundary("direct SaveCredential is policy guarded", () => db.SaveCredential(id, new("changed", "changed")), ("DisableCredentialStorage", 1));
        Boundary("direct ReadCredential is policy guarded", () => db.ReadCredential(id), ("DisableCredentialStorage", 1));
        Boundary("direct HasCredential is policy guarded", () => db.HasCredential(id), ("DisableCredentialStorage", 1));
        Boundary("direct ExportCredentials is policy guarded", () => db.ExportCredentials(), ("DisableCredentialStorage", 1));
        Boundary("direct ExportCredentials respects DisableExport", () => db.ExportCredentials(), ("DisableExport", 1));
        Boundary("ReplaceSnapshot refuses credential import", () => db.ReplaceSnapshot(new(), secrets), ("DisableCredentialStorage", 1));
        Boundary("ReplaceSnapshot refuses empty import without erasing existing secrets", () => db.ReplaceSnapshot(new(), new()), ("DisableCredentialStorage", 1));
        Boundary("ReplaceSnapshot respects disabled transfer", () => db.ReplaceSnapshot(new(), secrets), ("DisableExport", 1));
        Boundary("CredentialVault Save refuses clearly", () => vault.Save(id, credential), ("DisableCredentialStorage", 1));
        Boundary("CredentialVault ExportAll refuses rather than silently stripping secrets", () => vault.ExportAll(), ("DisableCredentialStorage", 1));
        Boundary("CredentialVault Read refuses fill disabled", () => vault.Read(id), ("DisableCredentialFill", 1));
        Boundary("direct ReadCredential refuses fill disabled", () => db.ReadCredential(id), ("DisableCredentialFill", 1));
        Boundary("malformed policy guards real storage", () => db.SaveCredential(id, credential), ("DisableCredentialStorage", "false"));
        test("KeePass fallback is not invoked under disabled credential storage", () =>
        {
            var before = KeePassXcService.Calls;
            using (Policy(("DisableCredentialStorage", 1)))
                Refused(() => vault.Read(new WebsiteWorkflow { KeepassEntryPath = "fixture" }, new Configuration { KeePassDatabasePath = "fixture" }));
            Check(KeePassXcService.Calls == before);
        });
        test("denied operations preserve decryptable vault across lock/unlock", () =>
        {
            db.Lock();
            using (Policy(("DisableCredentialStorage", 1))) Check(db.Unlock(Master));
            Check(db.ReadCredential(id) == credential);
        });
        test("explicit deletion remains possible without enabling storage", () =>
        {
            using (Policy(("DisableCredentialStorage", 1))) vault.Delete(id);
            Check(!db.HasCredential(id));
            db.SaveCredential(id, credential);
        });
        test("encrypted database has no synthetic plaintext secret", () =>
        {
            foreach (var path in Directory.GetFiles(db.DataDirectory))
                Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(credential.Password, StringComparison.Ordinal));
        });
        var exportPath = Path.Combine(root, "fixture.launchflow");
        const string exportPassword = "FixtureOnly-Export-19!";
        test("unmanaged encrypted transfer roundtrips and retains workflow identity", () =>
        {
            SecureTransferService.Export(exportPath, exportPassword, config, vault, includePasswords: true);
            Check(System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(exportPath), 0, 5) == "LFLW1");
            var package = SecureTransferService.Read(exportPath, exportPassword);
            Check(package.Credentials[id] == credential);
            Check(package.Config.Workflows.Single().Id == id);
            db.ReplaceSnapshot(package.Config, package.Credentials);
            Check(!db.LoadConfiguration().Workflows.Single().Enabled);
            Check(db.ReadCredential(id) == credential);
        });
        test("disabled transfer refuses passwordless export without touching existing target", () =>
        {
            var before = File.ReadAllBytes(exportPath);
            using (Policy(("DisableExport", 1)))
                Refused(() => SecureTransferService.Export(exportPath, exportPassword, config, vault, includePasswords: false));
            Check(before.SequenceEqual(File.ReadAllBytes(exportPath)));
        });
        test("disabled transfer refuses read before opening missing file", () =>
        {
            using (Policy(("DisableExport", 1))) Refused(() => SecureTransferService.Read(exportPath + ".missing", exportPassword));
        });
        test("storage-disabled import refuses before reading encrypted credentials", () =>
        {
            using (Policy(("DisableCredentialStorage", 1))) Refused(() => SecureTransferService.Read(exportPath, exportPassword));
        });
        test("storage-disabled password export refuses and leaves no file", () =>
        {
            var target = exportPath + ".denied";
            using (Policy(("DisableCredentialStorage", 1)))
                Refused(() => SecureTransferService.Export(target, exportPassword, config, vault, includePasswords: true));
            Check(!File.Exists(target));
        });
        test("storage-disabled passwordless export is still supported", () =>
        {
            var target = exportPath + ".config";
            using (Policy(("DisableCredentialStorage", 1)))
                SecureTransferService.Export(target, exportPassword, config, vault, includePasswords: false);
            var package = SecureTransferService.Read(target, exportPassword);
            Check(package.Credentials.Count == 0 && package.Config.Workflows.Single().Id == id);
        });
        test("wrong portable password refuses without replacing live vault", () =>
        {
            try { SecureTransferService.Read(exportPath, "WrongFixtureOnly-42!"); throw new Exception("Accepted wrong password"); }
            catch (InvalidOperationException) { }
            Check(db.ReadCredential(id) == credential);
        });
        db.Lock();
    }

    internal static IDisposable Policy(params (string Name, object? Value)[] values) =>
        EnterprisePolicy.UseProviderForTesting(() => values.ToDictionary(v => v.Name, v => v.Value));
    internal static void Check(bool value) { if (!value) throw new InvalidOperationException("Assertion failed"); }
    internal static void Refused(Action action)
    {
        try { action(); }
        catch (InvalidOperationException e) when (e.Message.Contains("organization", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("Expected an explicit organization-policy refusal.");
    }
    private static string Fingerprint(string directory) => string.Join("|", Directory.GetFiles(directory).Order().Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))));
}
