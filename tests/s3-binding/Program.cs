using System.Security.Cryptography;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Data.Sqlite;

var failed = 0;
var scenarios = 0;
void Check(bool condition, string label)
{
    scenarios++;
    Console.WriteLine((condition ? "PASS " : "FAIL ") + label);
    if (!condition) failed++;
}
string AuthoritativeTable(string path)
{
    using var c = new SqliteConnection("Data Source=" + path); c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='secure_credentials'";
    return cmd.ExecuteScalar() is null ? "credentials" : "secure_credentials";
}
byte[] GetBlob(string path, Guid id)
{
    using var c = new SqliteConnection("Data Source=" + path); c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT sealed FROM " + AuthoritativeTable(path) + " WHERE workflow_id=$id";
    cmd.Parameters.AddWithValue("$id", id.ToString("N"));
    return cmd.ExecuteScalar() as byte[] ?? throw new InvalidDataException("Missing authoritative credential row");
}
void SetBlob(string path, Guid id, byte[] value)
{
    using var c = new SqliteConnection("Data Source=" + path); c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = "UPDATE " + AuthoritativeTable(path) + " SET sealed=$blob WHERE workflow_id=$id";
    cmd.Parameters.AddWithValue("$id", id.ToString("N"));
    cmd.Parameters.AddWithValue("$blob", value);
    if (cmd.ExecuteNonQuery() != 1) throw new InvalidDataException("Missing attack target");
}
using (var f = new Fixture())
{
    f.Vault.Lock();
    var cold = new VaultDatabase(f.Path);
    var correct = cold.Unlock(Fixture.Master) &&
        cold.ReadCredential(f.A) == Fixture.Alpha &&
        cold.ReadCredential(f.B) == Fixture.Beta;
    cold.Lock();
    Check(correct, "UNALTERED_COLD_CONTROL");
}
foreach (var swap in new[] { false, true })
{
    using var f = new Fixture();
    f.Vault.Lock();
    var sealedValue = GetBlob(f.Path, swap ? f.A : f.B);
    if (!swap) sealedValue[12] ^= 1;
    SetBlob(f.Path, f.B, sealedValue);
    var cold = new VaultDatabase(f.Path);
    var admitted = cold.Unlock(Fixture.Master);
    var rejected = !admitted;
    SiteCredential? returned = null;
    if (admitted)
    {
        try { returned = cold.ReadCredential(f.B); }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
        { rejected = true; }
    }
    cold.Lock();
    Check(rejected && returned != Fixture.Alpha,
        swap ? "S3_WORKFLOW_SUBSTITUTION_REJECTED" : "BIT_FLIP_REJECTED_CONTROL");
}
byte[] ConfigBlob(string path)
{
    using var connection = new SqliteConnection("Data Source=" + path); connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT sealed FROM secure_objects WHERE id='config'";
    return (byte[])command.ExecuteScalar()!;
}
void Attack(string name, Action<Fixture> prepare, Func<Fixture, byte[]> replacement,
    string password = Fixture.Master)
{
    using var f = new Fixture();
    prepare(f);
    f.Vault.Lock();
    SetBlob(f.Path, f.B, replacement(f));
    var cold = new VaultDatabase(f.Path);
    var authorized = cold.Unlock(password);
    var rejected = !authorized;
    SiteCredential? received = null;
    if (authorized)
    {
        try { received = cold.ReadCredential(f.B); }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
        { rejected = true; }
    }
    cold.Lock();
    Check(rejected && received is null, name);
}
Attack("S3_REASSIGNMENT_AFTER_CREDENTIAL_SAVE",
    f => f.Vault.SaveCredential(f.A, new("edited@example.invalid", "changed-a-test")),
    f => GetBlob(f.Path, f.A));
Attack("S3_REASSIGNMENT_AFTER_IMPORT_REPLACEMENT",
    f => f.Vault.ReplaceSnapshot(f.Vault.LoadConfiguration(), new()
    {
        [f.A] = Fixture.Alpha, [f.B] = Fixture.Beta
    }, f.Vault.SessionGeneration),
    f => GetBlob(f.Path, f.A));
Attack("S3_REASSIGNMENT_AFTER_MASTER_ROTATION",
    f => f.Vault.ChangeMasterPassword(Fixture.Master, "Rotated!Synthetic-44"),
    f => GetBlob(f.Path, f.A),
    "Rotated!Synthetic-44");
Attack("S3_CONFIG_CIPHERTEXT_CANNOT_BECOME_CREDENTIAL",
    _ => { }, f => ConfigBlob(f.Path));
Attack("S3_STRIPPED_ENVELOPE_HEADER_REJECTED",
    _ => { }, f => GetBlob(f.Path, f.B)[4..]);
Attack("S3_TRUNCATED_ENVELOPE_REJECTED",
    _ => { }, f => GetBlob(f.Path, f.B)[..^1]);
Attack("S3_CHANGED_FORMAT_HEADER_REJECTED",
    _ => { }, f =>
    {
        var blob = GetBlob(f.Path, f.B);
        blob[0] ^= 1;
        return blob;
    });
Attack("S3_TRAILING_ENVELOPE_BYTES_REJECTED",
    _ => { }, f => GetBlob(f.Path, f.B).Concat(new byte[] { 0x3f }).ToArray());

using (var f = new Fixture())
{
    f.Vault.Lock();
    using (var sql = new SqliteConnection("Data Source=" + f.Path))
    {
        sql.Open();
        using var cmd = sql.CreateCommand();
        cmd.CommandText = "DELETE FROM secure_credentials WHERE workflow_id=$target;" +
            "UPDATE secure_credentials SET workflow_id=$target WHERE workflow_id=$source;";
        cmd.Parameters.AddWithValue("$target", f.B.ToString("N"));
        cmd.Parameters.AddWithValue("$source", f.A.ToString("N"));
        cmd.ExecuteNonQuery();
    }
    var cold = new VaultDatabase(f.Path);
    var rejected = !cold.Unlock(Fixture.Master);
    if (!rejected)
    {
        try { rejected = cold.ReadCredential(f.B) is null; }
        catch (CryptographicException) { rejected = true; }
    }
    Check(rejected, "S3_RELABELED_AUTHORITATIVE_ROW_REJECTED");
    cold.Lock();
}
using (var f = new Fixture())
{
    var previous = GetBlob(f.Path, f.B);
    f.Vault.SaveCredential(f.B, new("changed@example.invalid", "updated-b-test"));
    f.Vault.Lock();
    SetBlob(f.Path, f.B, previous);
    var cold = new VaultDatabase(f.Path);
    var replayAccepted = cold.Unlock(Fixture.Master) && cold.ReadCredential(f.B) == Fixture.Beta;
    Check(replayAccepted, "S3_SAME_OWNER_REPLAY_LIMIT_DOCUMENTED");
    cold.Lock();
}
using (var source = new Fixture())
using (var destination = new Fixture())
{
    var destinationConfiguration = destination.Vault.LoadConfiguration();
    source.Vault.Lock();
    destination.Vault.Lock();

    byte[] firstSalt, targetVaultId;
    using (var a = new SqliteConnection("Data Source=" + source.Path))
    using (var b = new SqliteConnection("Data Source=" + destination.Path))
    {
        a.Open(); b.Open();
        using var commandA = a.CreateCommand();
        commandA.CommandText = "SELECT salt FROM secure_meta WHERE id=1;";
        firstSalt = (byte[])commandA.ExecuteScalar()!;
        using var commandB = b.CreateCommand();
        commandB.CommandText = "SELECT vault_id FROM secure_meta WHERE id=1;";
        targetVaultId = (byte[])commandB.ExecuteScalar()!;
    }

    var derive = typeof(VaultDatabase).GetMethod("DeriveKey",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("Vault key derivation is missing");
    var sharedKey = (byte[])derive.Invoke(null,
        new object[] { Fixture.Master, firstSalt, 128 * 1024, 4, 2 })!;
    try
    {
        var verifier = VaultRecordCodec.Seal(
            System.Text.Encoding.UTF8.GetBytes("LaunchFlow.Vault.v3"),
            sharedKey, targetVaultId, "verifier", "verifier");
        var config = VaultRecordCodec.Seal(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(destinationConfiguration),
            sharedKey, targetVaultId, "config", "config");

        using (var db = new SqliteConnection("Data Source=" + destination.Path))
        {
            db.Open();
            using var update = db.CreateCommand();
            update.CommandText =
                "UPDATE secure_meta SET salt=$salt,verifier=$verifier WHERE id=1;" +
                "UPDATE secure_objects SET sealed=$config WHERE id='config';" +
                "DELETE FROM secure_credentials;";
            update.Parameters.AddWithValue("$salt", firstSalt);
            update.Parameters.AddWithValue("$verifier", verifier);
            update.Parameters.AddWithValue("$config", config);
            update.ExecuteNonQuery();
        }

        // Both vaults now use the same master key derivation inputs; before the
        // attack the destination has independently valid v3 verifier/config.
        var valid = new VaultDatabase(destination.Path);
        var healthy = valid.Unlock(Fixture.Master) &&
            valid.LoadConfiguration().Workflows.Count == 2;
        valid.Lock();
        Check(healthy, "S3_MATCHED_KEY_DISTINCT_VAULT_CONTROL");

        using (var db = new SqliteConnection("Data Source=" + destination.Path))
        {
            db.Open();
            using var insert = db.CreateCommand();
            insert.CommandText =
                "INSERT INTO secure_credentials(workflow_id,sealed) VALUES($owner,$value);";
            insert.Parameters.AddWithValue("$owner", source.A.ToString("N"));
            insert.Parameters.AddWithValue("$value", GetBlob(source.Path, source.A));
            insert.ExecuteNonQuery();
        }
        var cold = new VaultDatabase(destination.Path);
        var rejected = !cold.Unlock(Fixture.Master);
        if (!rejected)
        {
            try { rejected = cold.ReadCredential(source.A) is null; }
            catch (CryptographicException) { rejected = true; }
        }
        Check(rejected, "S3_CROSS_VAULT_TRANSPLANT_SAME_KEY_OWNER_REJECTED");
        cold.Lock();
    }
    finally { CryptographicOperations.ZeroMemory(sharedKey); }
}
Console.WriteLine($"S3_RESULT scenarios={scenarios} failures={failed}");
return failed == 0 ? 0 : 1;

sealed class Fixture : IDisposable
{
    public const string Master = "Disposable!Fixture-Only98";
    public static readonly SiteCredential Alpha = new("sample-a@example.invalid", "sample-a-test-value");
    public static readonly SiteCredential Beta = new("sample-b@example.invalid", "sample-b-test-value");
    public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "launchflow-s3-" + Guid.NewGuid().ToString("N"));
    public string Path { get; }
    public Guid A { get; } = Guid.NewGuid();
    public Guid B { get; } = Guid.NewGuid();
    public VaultDatabase Vault { get; }
    public Fixture()
    {
        Directory.CreateDirectory(Root);
        Path = System.IO.Path.Combine(Root, "synthetic.db");
        Vault = new VaultDatabase(Path);
        Vault.Initialize(Master, new Configuration { Workflows = new()
        {
            new WebsiteWorkflow { Id = A, Name = "A", StartUrl = "https://a.example.invalid" },
            new WebsiteWorkflow { Id = B, Name = "B", StartUrl = "https://b.example.invalid" }
        } }, new Dictionary<Guid, SiteCredential> { [A] = Alpha, [B] = Beta });
    }
    public void Dispose()
    {
        Vault.Lock();
        SqliteConnection.ClearAllPools();
        Directory.Delete(Root, true);
    }
}
