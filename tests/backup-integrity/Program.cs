using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Data.Sqlite;

var fixtureRoot = Environment.GetEnvironmentVariable("LAUNCHFLOW_FIXTURE_DATA_ROOT")
    ?? throw new InvalidOperationException("A private synthetic backup directory is required.");
var root = Path.GetFullPath(fixtureRoot);
var current = VaultDatabase.Current;
if (!Path.GetFullPath(current.DatabasePath).StartsWith(root + Path.DirectorySeparatorChar,
        StringComparison.Ordinal))
    throw new InvalidOperationException("Refusing tests outside the isolated synthetic fixture directory.");
if (current.Exists) throw new InvalidOperationException("The synthetic vault already exists.");

Directory.CreateDirectory(root);
const string master = "Synthetic!Backup-Master9";
const string exportKey = "Synthetic!Backup-Export8";
var workflow = Guid.NewGuid();
var saved = new SiteCredential("synthetic@example.invalid", "synthetic-backup-secret");
var config = new Configuration { Workflows = new()
{
    new WebsiteWorkflow { Id = workflow, Name = "Synthetic backup", StartUrl = "https://example.test" }
}};
var facade = new CredentialVault();
var passed = 0;
var failed = 0;
void Check(bool condition, string name)
{
    if (condition) { passed++; Console.WriteLine("PASS " + name); }
    else { failed++; Console.WriteLine("FAIL " + name); }
}
try
{
    current.Initialize(master, config, new Dictionary<Guid, SiteCredential> { [workflow] = saved });
    var inclusive = Path.Combine(root, "inclusive.launchflow");
    SecureTransferService.Export(inclusive, exportKey, config, facade, true);
    Check(PortableExport.Read(inclusive, exportKey).Credentials.TryGetValue(workflow, out var present) &&
          present == saved, "B1 inclusive export retains credential");

    var configOnly = Path.Combine(root, "configuration-only.launchflow");
    SecureTransferService.Export(configOnly, exportKey, config, facade, false);
    Check(PortableExport.Read(configOnly, exportKey).Credentials.Count == 0,
        "B1 explicit configuration-only backup omits passwords");

    var staleSession = current.SessionGeneration;
    current.Lock();
    var invalid = Path.Combine(root, "unavailable.launchflow");
    bool lockedRefused = false;
    try { SecureTransferService.Export(invalid, exportKey, config, facade, true, staleSession); }
    catch (InvalidOperationException) { lockedRefused = true; }
    Check(lockedRefused && !File.Exists(invalid), "B1 locked inclusive export refuses without creating file");

    current.Unlock(master);
    var stale = Path.Combine(root, "stale-session.launchflow");
    bool staleRejected = false;
    try { SecureTransferService.Export(stale, exportKey, config, facade, true, staleSession); }
    catch (InvalidOperationException) { staleRejected = true; }
    Check(staleRejected && !File.Exists(stale), "B1 stale lock-unlock session refuses export");

    var replacement = new PortableExport.PortablePackage
    {
        Config = config,
        Credentials = new Dictionary<Guid, SiteCredential>
        {
            [workflow] = new("new@example.invalid", "synthetic-updated-secret")
        }
    };
    PortableExport.Write(inclusive, exportKey, replacement);
    Check(PortableExport.Read(inclusive, exportKey).Credentials[workflow] ==
          replacement.Credentials[workflow], "B2 complete overwrite publishes recoverable replacement");
    var priorBytes = File.ReadAllBytes(inclusive);
    var invalidPasswordRejected = false;
    try { PortableExport.Write(inclusive, "weak", replacement); }
    catch (ArgumentException) { invalidPasswordRejected = true; }
    Check(invalidPasswordRejected && File.ReadAllBytes(inclusive).SequenceEqual(priorBytes),
        "B2 rejected staging input preserves existing backup");
    Check(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.TopDirectoryOnly).Any(),
        "B2 successful and rejected writes leave no staging files");

    current.DeleteCredential(workflow);
    var empty = Path.Combine(root, "authorized-empty.launchflow");
    SecureTransferService.Export(empty, exportKey, config, facade, true);
    Check(PortableExport.Read(empty, exportKey).Credentials.Count == 0,
        "B1 authorized empty password-inclusive export remains valid");
}
finally
{
    current.Lock();
    SqliteConnection.ClearAllPools();
}
Console.WriteLine($"BACKUP_INTEGRITY_SUMMARY passed={passed} failed={failed}");
return failed == 0 ? 0 : 1;
