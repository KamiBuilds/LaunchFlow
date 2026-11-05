using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Data.Sqlite;

const string OldMaster = "Synthetic!Vault-Master9";
const string NewMaster = "Changed!Vault-Master8";
var total = 0;
var failures = 0;
void Test(string name, Action action)
{
    total++;
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + " " + ex.Message);
    }
}
void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
void Sql(string path, string statement)
{
    using var c = new SqliteConnection("Data Source=" + path);
    c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = statement;
    cmd.ExecuteNonQuery();
}
void Preserved(VaultFixture f)
{
    Require(f.Vault.ReadCredential(f.A) == VaultFixture.ACredential, "live A changed");
    Require(f.Vault.ReadCredential(f.B) == VaultFixture.BCredential, "live B changed");
    f.Vault.Lock();
    using var cold = new ColdVault(f.Path);
    Require(cold.Vault.Unlock(OldMaster), "cold original unlock failed");
    Require(cold.Vault.ReadCredential(f.A) == VaultFixture.ACredential, "cold A changed");
    Require(cold.Vault.ReadCredential(f.B) == VaultFixture.BCredential, "cold B changed");
}
Test("S1 rejected first insert preserves existing credentials and cache", () =>
{
    using var f = new VaultFixture();
    Sql(f.Path, "CREATE TRIGGER reject_first BEFORE INSERT ON credentials WHEN NEW.workflow_id='" +
        f.A.ToString("N") + "' BEGIN SELECT RAISE(ABORT,'synthetic-rejection'); END;");
    var rejected = false;
    var target = Guid.NewGuid();
    try { f.Vault.SaveCredential(target, new("new@example.invalid", "synthetic-secret")); }
    catch (SqliteException) { rejected = true; }
    Require(rejected && f.Vault.ReadCredential(target) is null, "candidate was accepted or cached");
    Preserved(f);
});
Test("S1 rejected later insert rolls back every rewrite", () =>
{
    using var f = new VaultFixture();
    Sql(f.Path, "CREATE TRIGGER reject_later BEFORE INSERT ON credentials WHEN NEW.workflow_id='" +
        f.B.ToString("N") + "' BEGIN SELECT RAISE(ABORT,'synthetic-rejection'); END;");
    bool rejected = false;
    try { f.Vault.SaveCredential(Guid.NewGuid(), new("late@example.invalid", "synthetic-secret")); }
    catch (SqliteException) { rejected = true; }
    Require(rejected, "expected SQL insert rejection");
    Preserved(f);
});
Test("S1 rejected delete restores target and unrelated credentials", () =>
{
    using var f = new VaultFixture();
    Sql(f.Path, "CREATE TRIGGER reject_delete_rewrite BEFORE INSERT ON credentials WHEN NEW.workflow_id='" +
        f.B.ToString("N") + "' BEGIN SELECT RAISE(ABORT,'synthetic-rejection'); END;");
    bool rejected = false;
    try { f.Vault.DeleteCredential(f.A); }
    catch (SqliteException) { rejected = true; }
    Require(rejected, "expected SQL insert rejection");
    Preserved(f);
});
Test("S1 successful update and delete are durable and identity-specific", () =>
{
    using var f = new VaultFixture();
    var next = new SiteCredential("updated@example.invalid", "new-synthetic-secret");
    f.Vault.SaveCredential(f.A, next);
    Require(f.Vault.ReadCredential(f.A) == next && f.Vault.ReadCredential(f.B) == VaultFixture.BCredential,
        "save changed wrong account");
    f.Vault.DeleteCredential(f.B);
    f.Vault.Lock();
    using var cold = new ColdVault(f.Path);
    Require(cold.Vault.Unlock(OldMaster), "cold unlock failed");
    Require(cold.Vault.ReadCredential(f.A) == next && cold.Vault.ReadCredential(f.B) is null,
        "cold save/delete mismatch");
});
Test("S2 failed rotation cannot create a mixed-key later write", () =>
{
    using var f = new VaultFixture();
    Sql(f.Path, "CREATE TRIGGER reject_rekey BEFORE UPDATE ON objects WHEN NEW.id='config' " +
        "BEGIN SELECT RAISE(ABORT,'synthetic-rekey-rejection'); END;");
    bool rejected = false;
    try { f.Vault.ChangeMasterPassword(OldMaster, NewMaster); }
    catch (SqliteException) { rejected = true; }
    Require(rejected, "expected rotation failure");
    Sql(f.Path, "DROP TRIGGER reject_rekey;");
    using (var cold = new ColdVault(f.Path))
    {
        Require(cold.Vault.Unlock(OldMaster), "original disk password no longer works");
        Require(cold.Vault.ReadCredential(f.A) == VaultFixture.ACredential, "original credential corrupted");
    }
    if (f.Vault.IsUnlocked)
    {
        var config = f.Vault.LoadConfiguration();
        config.UiLanguage = "nl";
        f.Vault.SaveConfiguration(config);
    }
    f.Vault.Lock();
    using var verified = new ColdVault(f.Path);
    Require(verified.Vault.Unlock(OldMaster), "old verifier rejected after failed rotation");
    Require(verified.Vault.LoadConfiguration().UiLanguage is "en" or "nl", "later config save mismatched key");
    Require(verified.Vault.ReadCredential(f.A) == VaultFixture.ACredential, "later save lost credentials");
});
Test("S2 successful rotation revokes old credentials and old session", () =>
{
    using var f = new VaultFixture();
    var captured = f.Vault.SessionGeneration;
    f.Vault.ChangeMasterPassword(OldMaster, NewMaster);
    Require(f.Vault.SessionGeneration > captured, "session generation was not revoked");
    f.Vault.Lock();
    using var old = new ColdVault(f.Path);
    Require(!old.Vault.Unlock(OldMaster), "old password still unlocks");
    using var newer = new ColdVault(f.Path);
    Require(newer.Vault.Unlock(NewMaster), "new password does not unlock");
    Require(newer.Vault.ReadCredential(f.A) == VaultFixture.ACredential &&
        newer.Vault.ReadCredential(f.B) == VaultFixture.BCredential, "new key did not recover records");
    bool rejected = false;
    try { newer.Vault.SaveConfiguration(newer.Vault.LoadConfiguration(), captured); }
    catch (InvalidOperationException) { rejected = true; }
    Require(rejected, "stale session accepted a write");
});
Console.WriteLine($"STORAGE_INTEGRITY_SUMMARY passed={total - failures} failed={failures}");
return failures == 0 ? 0 : 1;

sealed class ColdVault : IDisposable
{
    public VaultDatabase Vault { get; }
    public ColdVault(string path) => Vault = new VaultDatabase(path);
    public void Dispose() => Vault.Lock();
}
sealed class VaultFixture : IDisposable
{
    public static readonly SiteCredential ACredential = new("alpha@example.invalid", "alpha-synthetic-secret");
    public static readonly SiteCredential BCredential = new("beta@example.invalid", "beta-synthetic-secret");
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "launchflow-storage-fixture-" + Guid.NewGuid().ToString("N"));
    public Guid A { get; } = Guid.NewGuid();
    public Guid B { get; } = Guid.NewGuid();
    public string Path { get; }
    public VaultDatabase Vault { get; }
    public VaultFixture()
    {
        Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, "vault.db");
        Vault = new VaultDatabase(Path);
        Vault.Initialize("Synthetic!Vault-Master9",
            new Configuration { UiLanguage = "en", Workflows = new()
            {
                new WebsiteWorkflow { Id = A, Name = "Fixture A", StartUrl = "https://a.example.test" },
                new WebsiteWorkflow { Id = B, Name = "Fixture B", StartUrl = "https://b.example.test" }
            }},
            new Dictionary<Guid, SiteCredential> { [A] = ACredential, [B] = BCredential });
    }
    public void Dispose()
    {
        Vault.Lock();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
