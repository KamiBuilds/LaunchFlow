using LaunchFlow;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Data.Sqlite;
using System.IO;

const string Master = "Synthetic!Vault-Master9";
const string ExportPassword = "Synthetic!Backup-Export8";
var controlledRoot = Environment.GetEnvironmentVariable("LAUNCHFLOW_FIXTURE_DATA_ROOT")
    ?? throw new InvalidOperationException("A disposable fixture root is required.");
var fixtureRoot = Path.GetFullPath(controlledRoot);
var vault = VaultDatabase.Current;
if (!Path.GetFullPath(vault.DatabasePath).StartsWith(fixtureRoot + Path.DirectorySeparatorChar,
        StringComparison.Ordinal))
    throw new InvalidOperationException("Refusing to use a vault outside the fixture root.");
if (vault.Exists) throw new InvalidOperationException("The fixture vault already exists.");

Directory.CreateDirectory(Path.GetDirectoryName(vault.DatabasePath)!);
var originalId = Guid.NewGuid();
var importedId = Guid.NewGuid();
var originalConfig = new Configuration
{
    UiLanguage = "en",
    Workflows = new() { new WebsiteWorkflow { Id = originalId, Name = "Original synthetic",
        StartUrl = "https://original.example.test" } }
};
var newConfig = new Configuration
{
    UiLanguage = "nl",
    Workflows = new() { new WebsiteWorkflow { Id = importedId, Name = "Replacement synthetic",
        StartUrl = "https://replacement.example.test" } }
};
var originalCredentials = new Dictionary<Guid, SiteCredential>
{ [originalId] = new("original@example.invalid", "original-synthetic-password") };
var nextCredentials = new Dictionary<Guid, SiteCredential>
{ [importedId] = new("replacement@example.invalid", "replacement-synthetic-password") };
vault.Initialize(Master, originalConfig, originalCredentials);
var encryptedImport = Path.Combine(fixtureRoot, "synthetic-import.launchflow");
PortableExport.Write(encryptedImport, ExportPassword,
    new PortableExport.PortablePackage { Config = newConfig, Credentials = nextCredentials });

var passed = 0;
var failed = 0;
void Require(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
}
void ResetState()
{
    Hooks.Reset();
    if (!vault.IsUnlocked) Require(vault.Unlock(Master), "Cannot unlock disposable fixture");
    vault.ReplaceSnapshot(originalConfig, originalCredentials);
}
void CheckCold(bool original)
{
    var fresh = new VaultDatabase(vault.DatabasePath);
    Require(fresh.Unlock(Master), "Independent cold instance could not unlock");
    var config = fresh.LoadConfiguration();
    var activeId = original ? originalId : importedId;
    var excludedId = original ? importedId : originalId;
    var credential = original ? originalCredentials[originalId] : nextCredentials[importedId];
    Require(config.Workflows.Count == 1 && config.Workflows[0].Id == activeId,
        "Cold configuration changed unexpectedly");
    Require(fresh.ReadCredential(activeId) == credential &&
            fresh.ReadCredential(excludedId) is null,
        "Cold credential state changed unexpectedly");
    fresh.Lock();
}
void Case(string name, Action<MainWindow> operation, bool expectOriginal)
{
    ResetState();
    var ui = new MainWindow(vault.LoadConfiguration());
    try
    {
        operation(ui);
        Require(ui.Imported != expectOriginal, "UI published an unauthorized import or rejected a valid one");
        Require(ui._configuration.Workflows.Count == 1 &&
            ui._configuration.Workflows[0].Id == (expectOriginal ? originalId : importedId),
            "Live configuration did not match accepted operation");
        CheckCold(expectOriginal);
        passed++;
        Console.WriteLine("PASS " + name);
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine("FAIL " + name + " " + ex.GetType().Name + ": " + ex.Message);
    }
    finally { Hooks.Reset(); }
}

Case("IMPORT_RELOCK_CONFIRM_REVOKES_OLD_APPROVAL", ui =>
{
    Hooks.ConfirmImport = () =>
    {
        vault.Lock();
        Require(vault.Unlock(Master), "Synthetic session could not be reestablished");
    };
    ui.Import(encryptedImport);
}, expectOriginal: true);

Case("IMPORT_RELOCK_PASSWORD_PROMPT_REVOKES_OLD_APPROVAL", ui =>
{
    Hooks.PasswordPrompt = () =>
    {
        vault.Lock();
        Require(vault.Unlock(Master), "Synthetic session could not be reestablished");
    };
    ui.Import(encryptedImport);
}, expectOriginal: true);

Case("IMPORT_REMAINS_LOCKED_REJECTED", ui =>
{
    Hooks.ConfirmImport = vault.Lock;
    ui.Import(encryptedImport);
}, expectOriginal: true);

Case("IMPORT_CONFIRMED_CONTINUOUS_SESSION", ui =>
{
    ui._keepassMasterPassword = "synthetic-cache-before-import";
    var previousCacheRevision = ui._keepassCacheRevision;
    ui.Import(encryptedImport);
    Require(ui._keepassMasterPassword is null &&
            ui._keepassCacheRevision == previousCacheRevision + 1,
        "Accepted import must revoke the prior KeePass cache ownership");
}, expectOriginal: false);

vault.Lock();
SqliteConnection.ClearAllPools();
Console.WriteLine($"IMPORT_INTEGRITY_SUMMARY passed={passed} failed={failed}");
return failed == 0 ? 0 : 1;
