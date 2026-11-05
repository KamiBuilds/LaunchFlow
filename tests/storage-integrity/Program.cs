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
    try { f.Vault.SaveConfiguration(newer.Vault.LoadConfiguration(), captured); }
    catch (InvalidOperationException) { rejected = true; }
    Require(rejected, "stale session accepted a write on the original vault instance");
});
Test("A3 second statement rejection rolls credentials and settings back together", () =>
{
    using var f = new VaultFixture();
    var original = f.Vault.LoadConfiguration();
    var initial = original.Workflows.Single(w => w.Id == f.A).AutoLogin;
    var candidate = f.Vault.LoadConfiguration();
    candidate.Workflows.Single(w => w.Id == f.A).AutoLogin = !initial;
    Sql(f.Path, "CREATE TRIGGER reject_combined_config BEFORE UPDATE ON objects WHEN NEW.id='config' " +
        "BEGIN SELECT RAISE(ABORT,'synthetic-config-rejection'); END;");
    bool rejected = false;
    try { f.Vault.SaveCredentialAndConfiguration(f.A,
        new SiteCredential("replacement@example.invalid", "synthetic-rejected-password"),
        candidate, f.Vault.SessionGeneration); }
    catch (SqliteException) { rejected = true; }
    Require(rejected && f.Vault.ReadCredential(f.A) == VaultFixture.ACredential &&
        f.Vault.LoadConfiguration().Workflows.Single(w => w.Id == f.A).AutoLogin == initial,
        "A3 failure changed live credential or settings");
    f.Vault.Lock();
    using var cold = new ColdVault(f.Path);
    Require(cold.Vault.Unlock(OldMaster) && cold.Vault.ReadCredential(f.A) == VaultFixture.ACredential &&
        cold.Vault.LoadConfiguration().Workflows.Single(w => w.Id == f.A).AutoLogin == initial,
        "A3 failure changed durable credential or settings");
});
Test("A3 valid combined save publishes both parts and rejects stale generation", () =>
{
    using var f = new VaultFixture();
    var candidate = f.Vault.LoadConfiguration();
    var site = candidate.Workflows.Single(w => w.Id == f.A);
    site.KeepassEntryPath = "synthetic-entry";
    site.AutoLogin = !site.AutoLogin;
    var replacement = new SiteCredential("replacement@example.invalid", "synthetic-replacement");
    var oldSession = f.Vault.SessionGeneration;
    f.Vault.SaveCredentialAndConfiguration(f.A, replacement, candidate, oldSession);
    f.Vault.Lock();
    using var cold = new ColdVault(f.Path);
    Require(cold.Vault.Unlock(OldMaster), "A3 cold unlock failed");
    Require(cold.Vault.ReadCredential(f.A) == replacement &&
        cold.Vault.ReadCredential(f.B) == VaultFixture.BCredential &&
        cold.Vault.LoadConfiguration().Workflows.Single(w => w.Id == f.A).KeepassEntryPath == "synthetic-entry",
        "A3 cold combined update did not preserve its parts");
    bool refused = false;
    try { f.Vault.SaveCredentialAndConfiguration(f.A, replacement, candidate, oldSession); }
    catch (InvalidOperationException) { refused = true; }
    Require(refused, "A3 stale vault session accepted another write");
});
(byte[] Salt, int Memory, int Passes, int Lanes) SaltMetadata(string path)
{
    using var connection = new SqliteConnection("Data Source=" + path);
    connection.Open();
    using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT salt,argon_memory_kb,argon_iterations,argon_lanes FROM vault_meta WHERE id=1";
    using var reader = cmd.ExecuteReader();
    if (!reader.Read()) throw new Exception("Vault key-derivation metadata was missing");
    return ((byte[])reader[0], reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
}
using (var first = new VaultFixture())
using (var second = new VaultFixture())
{
    var a = SaltMetadata(first.Path);
    var b = SaltMetadata(second.Path);
    var type = typeof(VaultDatabase);
    var method = type.GetMethod("DeriveKey",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("Production master-key derivation was missing");
    byte[] Derive(string password, (byte[] Salt, int Memory, int Passes, int Lanes) m) =>
        (byte[])method.Invoke(null, new object[] { password, m.Salt, m.Memory, m.Passes, m.Lanes })!;
    Test("KDF new vault salt and recorded Argon2id parameters", () =>
        Require(a.Salt.Length == 16 && a.Memory == 128 * 1024 && a.Passes == 4 && a.Lanes == 2,
            "new-vault salt or parameters unexpectedly changed"));
    Test("KDF random salts differ across new vaults", () =>
        Require(!a.Salt.AsSpan().SequenceEqual(b.Salt), "two fixture vaults reused a salt"));
    var derived = new List<byte[]>();
    try
    {
        var k1 = Derive(OldMaster, a); derived.Add(k1);
        var k2 = Derive(OldMaster, a); derived.Add(k2);
        var ks = Derive(OldMaster, b); derived.Add(ks);
        var kw = Derive("Synthetic!Wrong-Master8", a); derived.Add(kw);
        Test("KDF same password and salt reproduce the same key", () =>
            Require(k1.AsSpan().SequenceEqual(k2), "Argon2id derivation was inconsistent"));
        Test("KDF changing salt changes the derived key", () =>
            Require(!k1.AsSpan().SequenceEqual(ks), "salt was not included in key derivation"));
        Test("KDF changing password changes the derived key", () =>
            Require(!k1.AsSpan().SequenceEqual(kw), "password was not included in key derivation"));
    }
    finally
    {
        foreach (var key in derived) System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
    }
    first.Vault.Lock();
    using var cold = new ColdVault(first.Path);
    Test("KDF wrong password refuses a cold locked vault", () =>
        Require(!cold.Vault.Unlock("Synthetic!Wrong-Master8") && !cold.Vault.IsUnlocked,
            "incorrect password authorized a new session"));
    Test("KDF correct password recovers the encrypted credential", () =>
        Require(cold.Vault.Unlock(OldMaster) &&
            cold.Vault.ReadCredential(first.A) == VaultFixture.ACredential,
            "correct password failed to recover the original credential"));
}
Test("import replacement denies a revoked vault session under the storage gate", () =>
{
    using var f = new VaultFixture();
    var expected = f.Vault.SessionGeneration;
    var replacement = f.Vault.LoadConfiguration();
    replacement.UiLanguage = "nl";
    var next = new Dictionary<Guid, SiteCredential>
    {
        [f.A] = new("replacement@example.invalid", "replacement-synthetic-secret")
    };
    f.Vault.Lock();
    Require(f.Vault.Unlock(OldMaster), "Synthetic vault could not be unlocked again");
    bool refused = false;
    try { f.Vault.ReplaceSnapshot(replacement, next, expected); }
    catch (InvalidOperationException) { refused = true; }
    Require(refused && f.Vault.ReadCredential(f.A) == VaultFixture.ACredential &&
        f.Vault.ReadCredential(f.B) == VaultFixture.BCredential,
        "Revoked import session wrote credentials or changed the live cache");
    f.Vault.Lock();
    using var cold = new ColdVault(f.Path);
    Require(cold.Vault.Unlock(OldMaster) && cold.Vault.LoadConfiguration().UiLanguage == "en" &&
        cold.Vault.ReadCredential(f.A) == VaultFixture.ACredential &&
        cold.Vault.ReadCredential(f.B) == VaultFixture.BCredential,
        "Revoked import session modified cold encrypted storage");
});
Test("import replacement succeeds with current session and preserves identity", () =>
{
    using var f = new VaultFixture();
    var config = f.Vault.LoadConfiguration();
    config.UiLanguage = "nl";
    var expected = f.Vault.SessionGeneration;
    f.Vault.ReplaceSnapshot(config,
        new Dictionary<Guid, SiteCredential> { [f.A] = VaultFixture.ACredential },
        expected);
    f.Vault.Lock();
    using var cold = new ColdVault(f.Path);
    Require(cold.Vault.Unlock(OldMaster) && cold.Vault.LoadConfiguration().UiLanguage == "nl" &&
        cold.Vault.ReadCredential(f.A) == VaultFixture.ACredential &&
        cold.Vault.ReadCredential(f.B) is null,
        "Authorized import did not atomically publish its configuration and credentials");
});
Test("initialization failure cannot publish a live or cold authorized vault", () =>
{
    var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "launchflow-init-failure-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var path = System.IO.Path.Combine(root, "vault.db");
    var candidate = new VaultDatabase(path);
    using var document = System.Text.Json.JsonDocument.Parse("{\"fault\":true}");
    var disposedJson = document.RootElement;
    document.Dispose();
    var invalidConfig = new Configuration
    {
        UiLanguage = "en",
        AdditionalSettings = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["synthetic-invalid-json"] = disposedJson
        }
    };
    try
    {
        bool rejected = false;
        try { candidate.Initialize(OldMaster, invalidConfig); }
        catch (ObjectDisposedException) { rejected = true; }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected && !candidate.IsUnlocked,
            "rejected vault initialization exposed an unlocked key");
        if (File.Exists(path))
        {
            var cold = new VaultDatabase(path);
            bool admitted = false;
            try { admitted = cold.Unlock(OldMaster); }
            catch (InvalidOperationException) { }
            Require(!admitted && !cold.IsUnlocked,
                "partly initialized metadata authorized a cold session");
        }
    }
    finally
    {
        candidate.Lock();
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }
});
Test("failed legacy credential migration does not publish an unlocked session", () =>
{
    using var f = new VaultFixture();
    f.Vault.Lock();
    Sql(f.Path, "DELETE FROM credentials; " +
        "INSERT OR REPLACE INTO objects(id,sealed) VALUES('credentials',X'000102030405060708090a0b0c0d0e0f');");
    var cold = new VaultDatabase(f.Path);
    var previousGeneration = cold.SessionGeneration;
    var admitted = cold.Unlock(OldMaster);
    Require(!admitted && !cold.IsUnlocked &&
        cold.SessionGeneration == previousGeneration,
        "failed legacy migration published a new vault authorization");
    cold.Lock();
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
