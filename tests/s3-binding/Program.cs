using System.Security.Cryptography;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Data.Sqlite;

var failed = 0;
void Check(bool condition, string label)
{
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
Console.WriteLine($"S3_RESULT scenarios=3 failures={failed}");
return failed == 0 ? 0 : 1;

sealed class Fixture : IDisposable
{
    public const string Master = "Disposable!Fixture-Only98";
    public static readonly SiteCredential Alpha = new("sample-a@example.invalid", "sample-a-test-value");
    public static readonly SiteCredential Beta = new("sample-b@example.invalid", "sample-b-test-value");
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "launchflow-s3-" + Guid.NewGuid().ToString("N"));
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
