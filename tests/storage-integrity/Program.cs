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
byte[] ReadDatabaseBytes(string path)
{
    // SQLite may retain a read/write handle in its native pool on Windows.
    // Sharing read and write access lets the test compare physical bytes
    // without trying to acquire an exclusive Windows file lock.
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    return memory.ToArray();
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
    Sql(f.Path, "CREATE TRIGGER reject_first BEFORE INSERT ON secure_credentials WHEN NEW.workflow_id='" +
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
    Sql(f.Path, "CREATE TRIGGER reject_later BEFORE INSERT ON secure_credentials WHEN NEW.workflow_id='" +
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
    Sql(f.Path, "CREATE TRIGGER reject_delete_rewrite BEFORE INSERT ON secure_credentials WHEN NEW.workflow_id='" +
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
    Sql(f.Path, "CREATE TRIGGER reject_rekey BEFORE UPDATE ON secure_objects WHEN NEW.id='config' " +
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
    Sql(f.Path, "CREATE TRIGGER reject_combined_config BEFORE UPDATE ON secure_objects WHEN NEW.id='config' " +
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
    cmd.CommandText = "SELECT salt,argon_memory_kb,argon_iterations,argon_lanes FROM secure_meta WHERE id=1";
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
Test("corrupted strict credential blocks cold unlock and session publication", () =>
{
    using var f = new VaultFixture();
    f.Vault.Lock();
    Sql(f.Path, "UPDATE secure_credentials SET sealed=X'000102030405060708090a0b0c0d0e0f' " +
        "WHERE workflow_id='" + f.A.ToString("N") + "';");
    var cold = new VaultDatabase(f.Path);
    var previousGeneration = cold.SessionGeneration;
    var admitted = cold.Unlock(OldMaster);
    Require(!admitted && !cold.IsUnlocked &&
        cold.SessionGeneration == previousGeneration,
        "Tampered v3 credentials authorized a vault session");
    cold.Lock();
});
Test("recovery inspection distinguishes an absent file without creating storage", () =>
{
    var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "launchflow-absent-recovery-" + Guid.NewGuid().ToString("N"));
    var path = System.IO.Path.Combine(root, "launchflow.db");
    var recovery = new VaultDatabase(path);
    Require(recovery.InspectStorage() == VaultStorageState.Absent &&
        !recovery.Exists && !File.Exists(path) && !Directory.Exists(root),
        "Inspecting absent storage created data or advertised a vault");
});
Test("recovery inspection recognizes complete encrypted vault without decryption", () =>
{
    using var f = new VaultFixture();
    f.Vault.Lock();
    var before = ReadDatabaseBytes(f.Path);
    var state = f.Vault.InspectStorage();
    var after = ReadDatabaseBytes(f.Path);
    Require(state == VaultStorageState.PresentNeedsAuthentication &&
        before.SequenceEqual(after) && !f.Vault.IsUnlocked,
        "Inspecting a completed locked vault changed storage or unlocked it");
    var cold = new VaultDatabase(f.Path);
    Require(cold.Unlock(OldMaster) && cold.ReadCredential(f.A) == VaultFixture.ACredential,
        "Inspection broke normal cold unlock");
    cold.Lock();
});
void CreateLegacyFixture(string path, bool singleBlob, int credentialCount = 1)
{
    var config = new Configuration { UiLanguage = "en", Workflows = new()
    {
        new WebsiteWorkflow { Id = Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Name = "Original A", StartUrl = "https://a.example.invalid", Enabled = true }
    } };
    var secrets = new Dictionary<Guid, SiteCredential>();
    if (credentialCount > 0) secrets[config.Workflows[0].Id] = VaultFixture.ACredential;
    if (credentialCount > 1)
    {
        var another = new WebsiteWorkflow {
            Id = Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Name = "Original B", StartUrl = "https://b.example.invalid", Enabled = true
        };
        config.Workflows.Add(another);
        secrets[another.Id] = VaultFixture.BCredential;
    }
    if (credentialCount < 0 || credentialCount > 2)
        throw new ArgumentOutOfRangeException(nameof(credentialCount));
    var type = typeof(VaultDatabase);
    var derive = type.GetMethod("DeriveKey", System.Reflection.BindingFlags.NonPublic |
        System.Reflection.BindingFlags.Static) ?? throw new Exception("Missing legacy derivation");
    var seal = type.GetMethod("Encrypt", System.Reflection.BindingFlags.NonPublic |
        System.Reflection.BindingFlags.Static) ?? throw new Exception("Missing legacy codec");
    var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
    var key = (byte[])derive.Invoke(null, new object[] { OldMaster, salt, 128 * 1024, 4, 2 })!;
    byte[] LegacySeal(byte[] plain) => (byte[])seal.Invoke(null, new object[] { plain, key })!;
    try
    {
        using var connection = new SqliteConnection("Data Source=" + path); connection.Open();
        using var schema = connection.CreateCommand();
        schema.CommandText =
            "CREATE TABLE vault_meta (id INTEGER PRIMARY KEY,salt BLOB,verifier BLOB," +
            "argon_memory_kb INTEGER,argon_iterations INTEGER,argon_lanes INTEGER,schema_version INTEGER);" +
            "CREATE TABLE objects(id TEXT PRIMARY KEY,sealed BLOB NOT NULL);" +
            (singleBlob ? "" : "CREATE TABLE credentials(workflow_id TEXT PRIMARY KEY,sealed BLOB NOT NULL);");
        schema.ExecuteNonQuery();
        using var meta = connection.CreateCommand();
        meta.CommandText = "INSERT INTO vault_meta VALUES(1,$salt,$v,131072,4,2,$version);";
        meta.Parameters.AddWithValue("$salt", salt);
        meta.Parameters.AddWithValue("$v", LegacySeal(System.Text.Encoding.UTF8.GetBytes("LaunchFlow.Vault.v1")));
        meta.Parameters.AddWithValue("$version", singleBlob ? 1 : 2);
        meta.ExecuteNonQuery();
        using var c = connection.CreateCommand();
        c.CommandText = "INSERT INTO objects VALUES('config',$s);";
        c.Parameters.AddWithValue("$s", LegacySeal(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(config)));
        c.ExecuteNonQuery();
        if (singleBlob)
        {
            using var b = connection.CreateCommand();
            b.CommandText = "INSERT INTO objects VALUES('credentials',$s);";
            b.Parameters.AddWithValue("$s", LegacySeal(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(secrets)));
            b.ExecuteNonQuery();
        }
        else
        {
            foreach (var pair in secrets)
            {
                using var row = connection.CreateCommand();
                row.CommandText = "INSERT INTO credentials VALUES($id,$s);";
                row.Parameters.AddWithValue("$id", pair.Key.ToString("N"));
                row.Parameters.AddWithValue("$s", LegacySeal(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(pair.Value)));
                row.ExecuteNonQuery();
            }
        }
    }
    finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }
}

Test("recovery inspection recognizes a legacy v1 vault without altering metadata", () =>
{
    var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "launchflow-legacy-v1-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var path = System.IO.Path.Combine(root, "legacy.db");
    try
    {
        CreateLegacyFixture(path, singleBlob: true);
        var before = ReadDatabaseBytes(path);
        var db = new VaultDatabase(path);
        Require(db.InspectStorage() == VaultStorageState.LegacyUpgradeRequired &&
            ReadDatabaseBytes(path).SequenceEqual(before) && !db.Unlock(OldMaster),
            "A legacy v1 vault was modified or allowed normal unconsented unlock");
        using var connection = new SqliteConnection("Data Source=" + path); connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT schema_version FROM vault_meta WHERE id=1;";
        Require(Convert.ToInt64(query.ExecuteScalar()) == 1L,
            "Read-only inspection rewrote the legacy schema version");
    }
    finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
});
Test("recovery inspection flags a truncated SQLite file without deletion", () =>
{
    var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "launchflow-truncated-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var path = System.IO.Path.Combine(root, "launchflow.db");
    var bytes = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };
    try
    {
        File.WriteAllBytes(path, bytes);
        var recovery = new VaultDatabase(path);
        Require(recovery.Exists &&
            recovery.InspectStorage() == VaultStorageState.IncompleteOrUnsupported &&
            ReadDatabaseBytes(path).SequenceEqual(bytes),
            "Malformed database was modified or treated as absent/usable");
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }
});
Test("recovery inspection flags metadata-only interrupted creation", () =>
{
    var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "launchflow-partial-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var path = System.IO.Path.Combine(root, "launchflow.db");
    try
    {
        Sql(path, "CREATE TABLE vault_meta (" +
            "id INTEGER PRIMARY KEY,salt BLOB,verifier BLOB,argon_memory_kb INTEGER," +
            "argon_iterations INTEGER,argon_lanes INTEGER,schema_version INTEGER);" +
            "INSERT INTO vault_meta VALUES(1,zeroblob(16),zeroblob(28),131072,4,2,2);" +
            "CREATE TABLE objects (id TEXT PRIMARY KEY,sealed BLOB NOT NULL);");
        var before = ReadDatabaseBytes(path);
        Require(new VaultDatabase(path).InspectStorage() == VaultStorageState.IncompleteOrUnsupported &&
            ReadDatabaseBytes(path).SequenceEqual(before),
            "Partial initialization was not detected or its bytes were altered");
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }
});
Test("recovery inspection flags malformed KDF metadata without changing it", () =>
{
    using var f = new VaultFixture();
    f.Vault.Lock();
    Sql(f.Path, "UPDATE secure_meta SET salt=X'01',argon_memory_kb=0 WHERE id=1;");
    var prior = ReadDatabaseBytes(f.Path);
    Require(f.Vault.InspectStorage() == VaultStorageState.IncompleteOrUnsupported &&
        ReadDatabaseBytes(f.Path).SequenceEqual(prior),
        "Malformed KDF metadata was accepted or rewritten");
});
Test("recovery inspection flags unsupported future schema without downgrade", () =>
{
    using var f = new VaultFixture();
    f.Vault.Lock();
    Sql(f.Path, "PRAGMA ignore_check_constraints=ON; UPDATE secure_meta SET schema_version=999 WHERE id=1;");
    Require(f.Vault.InspectStorage() == VaultStorageState.IncompleteOrUnsupported,
        "Unsupported future format was presented as authorized");
    using var c = new SqliteConnection("Data Source=" + f.Path);
    c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT schema_version FROM secure_meta WHERE id=1;";
    Require(Convert.ToInt64(cmd.ExecuteScalar()) == 999L,
        "Unsupported metadata was silently downgraded");
});
Test("recovery inspection flags missing sealed configuration without reset", () =>
{
    using var f = new VaultFixture();
    f.Vault.Lock();
    Sql(f.Path, "DELETE FROM secure_objects WHERE id='config';");
    Require(f.Vault.InspectStorage() == VaultStorageState.IncompleteOrUnsupported,
        "Missing configuration was treated as a valid setup");
    var cold = new VaultDatabase(f.Path);
    Require(!cold.Unlock(OldMaster) && !cold.IsUnlocked,
        "A missing sealed configuration authorized a cold vault");
    cold.Lock();
});
Test("recovery inspection recognizes valid new creation after interrupted-file checks", () =>
{
    var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "launchflow-valid-recovery-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var path = System.IO.Path.Combine(root, "launchflow.db");
    var vault = new VaultDatabase(path);
    try
    {
        Require(vault.InspectStorage() == VaultStorageState.Absent, "Fresh location is not absent");
        vault.Initialize(OldMaster, new Configuration { UiLanguage = "nl" });
        vault.Lock();
        Require(vault.InspectStorage() == VaultStorageState.PresentNeedsAuthentication,
            "Successful creation was not recognized as requiring authentication");
        var cold = new VaultDatabase(path);
        Require(cold.Unlock(OldMaster) && cold.LoadConfiguration().UiLanguage == "nl",
            "Newly created vault could not be reopened");
        cold.Lock();
    }
    finally
    {
        vault.Lock();
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }
});
Test("recovery inspection rejects a schema-v2 vault missing the credentials table", () =>
{
    using var f = new VaultFixture();
    f.Vault.Lock();
    Sql(f.Path, "DROP TABLE secure_credentials;");
    Require(f.Vault.InspectStorage() == VaultStorageState.IncompleteOrUnsupported,
        "Incomplete schema-v2 storage was misclassified as a recognized vault");
});
Test("legacy v1 layout remains recognizable and needs explicit upgrade", () =>
{
    var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "launchflow-legacy-blob-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var path = System.IO.Path.Combine(root, "legacy.db");
    try
    {
        CreateLegacyFixture(path, singleBlob: true);
        Require(new VaultDatabase(path).InspectStorage() == VaultStorageState.LegacyUpgradeRequired,
            "Legacy v1 single-blob credential layout was misclassified");
    }
    finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
});
Test("consented upgrade retains v1 and v2 credentials, disables auto-run and keeps recovery copies", () =>
{
    foreach (var blob in new[] { true, false })
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "launchflow-legacy-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = System.IO.Path.Combine(root, "legacy.db");
        try
        {
            CreateLegacyFixture(path, blob);
            var vault = new VaultDatabase(path);
            var before = ReadDatabaseBytes(path);
            Require(vault.InspectStorage() == VaultStorageState.LegacyUpgradeRequired &&
                !vault.Unlock(OldMaster) && !vault.IsUnlocked &&
                before.SequenceEqual(ReadDatabaseBytes(path)),
                "Unconsented legacy read changed or authorized storage");
            bool wrongDenied = false;
            try { vault.UpgradeLegacyVault("Synthetic!Wrong-Master8"); }
            catch (UnauthorizedAccessException) { wrongDenied = true; }
            Require(wrongDenied && before.SequenceEqual(ReadDatabaseBytes(path)),
                "Wrong-password upgrade modified the legacy database");
            var backup = vault.UpgradeLegacyVault(OldMaster);
            Require(File.Exists(backup) && vault.IsUnlocked &&
                vault.ReadCredential(Guid.Parse("11111111-1111-4111-8111-111111111111")) ==
                    VaultFixture.ACredential &&
                vault.LoadConfiguration().Workflows.Single().Enabled == false,
                "Upgrade did not preserve the credential or disable automatic execution");
            vault.Lock();
            var cold = new VaultDatabase(path);
            Require(cold.InspectStorage() == VaultStorageState.PresentNeedsAuthentication &&
                cold.Unlock(OldMaster) && cold.ReadCredential(
                    Guid.Parse("11111111-1111-4111-8111-111111111111")) ==
                    VaultFixture.ACredential,
                "Strict cold unlock lost migrated data");
            cold.Lock();
            var oldBackup = new VaultDatabase(backup);
            Require(oldBackup.InspectStorage() == VaultStorageState.LegacyUpgradeRequired &&
                !oldBackup.Unlock(OldMaster),
                "Recovery copy was changed or allowed unconsented runtime use");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
});
Test("legacy upgrade preserves zero and multiple credentials for both historical layouts", () =>
{
    foreach (var blob in new[] { true, false })
    foreach (var count in new[] { 0, 2 })
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "launchflow-count-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = System.IO.Path.Combine(root, "legacy.db");
        try
        {
            CreateLegacyFixture(path, blob, count);
            var vault = new VaultDatabase(path);
            var backup = vault.UpgradeLegacyVault(OldMaster);
            Require(File.Exists(backup) && vault.IsUnlocked,
                "Legacy zero/multiple upgrade did not complete with a recovery copy");
            vault.Lock();
            var cold = new VaultDatabase(path);
            Require(cold.Unlock(OldMaster), "Converted vault did not cold-open");
            var config = cold.LoadConfiguration();
            Require(config.Workflows.Count == (count == 2 ? 2 : 1) &&
                config.Workflows.All(w => !w.Enabled),
                "Migrated workflow state or disabled execution did not persist");
            var idA = Guid.Parse("11111111-1111-4111-8111-111111111111");
            var idB = Guid.Parse("22222222-2222-4222-8222-222222222222");
            Require(cold.ReadCredential(idA) ==
                (count == 0 ? null : VaultFixture.ACredential) &&
                cold.ReadCredential(idB) ==
                (count == 2 ? VaultFixture.BCredential : null),
                "Legacy credential count or workflow binding changed");
            cold.Lock();
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
});
Test("legacy WAL-active backup includes committed WAL state", () =>
{
    var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "launchflow-wal-upgrade-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var path = System.IO.Path.Combine(root, "legacy.db");
    try
    {
        CreateLegacyFixture(path, singleBlob: false);
        using (var keeper = new SqliteConnection("Data Source=" + path))
        {
            keeper.Open();
            using (var pragma = keeper.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;";
                pragma.ExecuteNonQuery();
            }
            using (var writer = keeper.CreateCommand())
            {
                writer.CommandText = "CREATE TABLE wal_proof(value TEXT);" +
                    "INSERT INTO wal_proof(value) VALUES('committed-wal-marker');";
                writer.ExecuteNonQuery();
            }
            Require(File.Exists(path + "-wal"), "Fixture did not retain a live WAL file");
            var vault = new VaultDatabase(path);
            var backup = vault.UpgradeLegacyVault(OldMaster);
            vault.Lock();
            using var restored = new SqliteConnection("Data Source=" + backup);
            restored.Open();
            using var read = restored.CreateCommand();
            read.CommandText = "SELECT value FROM wal_proof LIMIT 1;";
            Require((string?)read.ExecuteScalar() == "committed-wal-marker",
                "Verified backup did not include committed data in active WAL");
        }
    }
    finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
});
Test("failed recovery destination creation leaves legacy vault and bytes unchanged", () =>
{
    var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "launchflow-backup-refusal-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var path = System.IO.Path.Combine(root, "legacy.db");
    try
    {
        CreateLegacyFixture(path, singleBlob: false);
        var prior = ReadDatabaseBytes(path);
        File.WriteAllText(System.IO.Path.Combine(root, "recovery"),
            "synthetic-blocker-prevents-directory-creation");
        var vault = new VaultDatabase(path);
        bool refused = false;
        try { vault.UpgradeLegacyVault(OldMaster); }
        catch (IOException) { refused = true; }
        Require(refused && !vault.IsUnlocked &&
            vault.InspectStorage() == VaultStorageState.LegacyUpgradeRequired &&
            ReadDatabaseBytes(path).SequenceEqual(prior),
            "Failed recovery-copy preparation unexpectedly changed the legacy vault");
    }
    finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
});
Test("credential storage restriction refuses conversion without silently dropping legacy secrets", () =>
{
    var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "launchflow-policy-upgrade-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var path = System.IO.Path.Combine(root, "legacy.db");
    try
    {
        CreateLegacyFixture(path, singleBlob: false, credentialCount: 2);
        var before = ReadDatabaseBytes(path);
        using var restriction = EnterprisePolicy.UseProviderForTesting(() =>
            new Dictionary<string, object?> { ["DisableCredentialStorage"] = 1 });
        var vault = new VaultDatabase(path);
        bool blocked = false;
        try { vault.UpgradeLegacyVault(OldMaster); }
        catch (InvalidOperationException) { blocked = true; }
        Require(blocked && !vault.IsUnlocked &&
            vault.InspectStorage() == VaultStorageState.LegacyUpgradeRequired &&
            ReadDatabaseBytes(path).SequenceEqual(before) &&
            !Directory.Exists(System.IO.Path.Combine(root, "recovery")),
            "Credential storage restriction discarded or rewrote legacy data");
    }
    finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
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
