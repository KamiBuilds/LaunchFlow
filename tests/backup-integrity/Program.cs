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
    Check(SecureTransferService.Read(inclusive, exportKey).Credentials.TryGetValue(workflow, out var present) &&
          present == saved, "B1 inclusive export round-trips through the real import validator");

    var configOnly = Path.Combine(root, "configuration-only.launchflow");
    SecureTransferService.Export(configOnly, exportKey, config, facade, false);
    Check(SecureTransferService.Read(configOnly, exportKey).Credentials.Count == 0,
        "B1 configuration-only backup round-trips and omits passwords");

    // Existing workflows can still hold Fill actions from older versions.
    // Do not write a backup that this same app would reject on import, and
    // do not erase an older, valid backup if such an export is attempted.
    var legacyConfig = new Configuration { Workflows = new()
    {
        new WebsiteWorkflow
        {
            Id = workflow, Name = "Legacy Fill site", StartUrl = "https://example.test/form",
            Steps = new() { new WorkflowStep { Type = "Fill", Target = "#search", Value = "legacy-private-text" } }
        }
    }};
    current.SaveConfiguration(legacyConfig);
    var validPriorBackup = File.ReadAllBytes(inclusive);
    var legacyTarget = Path.Combine(root, "legacy-fill-target.launchflow");
    File.WriteAllBytes(legacyTarget, validPriorBackup);
    bool legacyRejected = false;
    string legacyMessage = "";
    try { SecureTransferService.Export(legacyTarget, exportKey, config, facade, true); }
    catch (InvalidOperationException ex) { legacyRejected = true; legacyMessage = ex.Message; }
    Check(legacyRejected && legacyMessage.Contains("Fill action", StringComparison.Ordinal) &&
          !legacyMessage.Contains("legacy-private-text", StringComparison.Ordinal) &&
          File.ReadAllBytes(legacyTarget).SequenceEqual(validPriorBackup) &&
          SecureTransferService.Read(legacyTarget, exportKey).Credentials[workflow] == saved,
          "B3 unsafe legacy Fill export refuses with actionable message and preserves previous restorable backup");

    var brandNewFillTarget = Path.Combine(root, "legacy-fill-new.launchflow");
    bool configOnlyRejected = false;
    try { SecureTransferService.Export(brandNewFillTarget, exportKey, config, facade, false); }
    catch (InvalidOperationException) { configOnlyRejected = true; }
    Check(configOnlyRejected && !File.Exists(brandNewFillTarget) &&
          !Directory.EnumerateFiles(root, "*.tmp", SearchOption.TopDirectoryOnly).Any(),
          "B3 config-only export cannot smuggle Fill values or leave staging files");

    // This guards against other invalid action shapes being exported, not just Fill.
    legacyConfig.Workflows[0].Steps = new()
    {
        new WorkflowStep { Type = "Click", Target = "#safe", Value = "unexpected-text" }
    };
    current.SaveConfiguration(legacyConfig);
    bool typedRejected = false;
    try { SecureTransferService.Export(brandNewFillTarget, exportKey, config, facade, true); }
    catch (InvalidOperationException) { typedRejected = true; }
    Check(typedRejected && !File.Exists(brandNewFillTarget),
          "B3 export checks every recorded action against the same import validator");
    current.SaveConfiguration(config);

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

    // Import validation: decrypted packages with hostile URLs / action types never publish.
    var hostile = Path.Combine(root, "hostile.launchflow");
    PortableExport.Write(hostile, exportKey, new PortableExport.PortablePackage
    {
        Config = new Configuration
        {
            Workflows =
            {
                new WebsiteWorkflow
                {
                    Id = Guid.NewGuid(),
                    Name = "'; DROP TABLE secure_credentials;--",
                    StartUrl = "javascript:alert(1)"
                }
            }
        },
        Credentials = new()
    });
    var hostileRejected = false;
    try { SecureTransferService.Read(hostile, exportKey); }
    catch (InvalidDataException) { hostileRejected = true; }
    Check(hostileRejected, "B3 hostile javascript: import URL is refused after decrypt");

    var sqlNamed = Path.Combine(root, "sql-named.launchflow");
    var sqlId = Guid.NewGuid();
    PortableExport.Write(sqlNamed, exportKey, new PortableExport.PortablePackage
    {
        Config = new Configuration
        {
            Workflows =
            {
                new WebsiteWorkflow
                {
                    Id = sqlId,
                    Name = "'; DROP TABLE secure_credentials;--",
                    StartUrl = "https://example.test/safe"
                }
            }
        },
        Credentials = new()
    });
    var accepted = SecureTransferService.Read(sqlNamed, exportKey);
    Check(accepted.Config.Workflows.Single().Name.Contains("DROP TABLE", StringComparison.Ordinal),
        "B3 SQL-looking website names stay data, never query text");
    VaultDatabase.Current.ReplaceSnapshot(accepted.Config, accepted.Credentials);
    var reloaded = VaultDatabase.Current.LoadConfiguration();
    Check(reloaded.Workflows.Single().Name.Contains("DROP TABLE", StringComparison.Ordinal) &&
          VaultDatabase.Current.Exists,
        "B3 parameterized vault write survives SQL metacharacters in website name");

    var fillPayload = Path.Combine(root, "fill-payload.launchflow");
    PortableExport.Write(fillPayload, exportKey, new PortableExport.PortablePackage
    {
        Config = new Configuration
        {
            Workflows =
            {
                new WebsiteWorkflow
                {
                    Id = Guid.NewGuid(),
                    Name = "Fill carrier",
                    StartUrl = "https://example.test/form",
                    Steps =
                    {
                        new WorkflowStep { Type = "Fill", Target = "#password", Value = "secret-from-backup" }
                    }
                }
            }
        },
        Credentials = new()
    });
    var fillRejected = false;
    try { SecureTransferService.Read(fillPayload, exportKey); }
    catch (InvalidDataException) { fillRejected = true; }
    Check(fillRejected, "B3 Fill steps with typed values are refused on import");

    // Older vaults can legitimately retain credential rows after a website
    // has been removed. Portable backups must preserve those encrypted rows
    // for legacy recovery, rather than reject or silently discard them.
    var orphanId = Guid.NewGuid();
    var orphanSecret = new SiteCredential("orphan@example.invalid", "synthetic-orphan-secret");
    var orphanPath = Path.Combine(root, "orphan-credential.launchflow");
    PortableExport.Write(orphanPath, exportKey, new PortableExport.PortablePackage
    {
        Config = config,
        Credentials = new Dictionary<Guid, SiteCredential> { [orphanId] = orphanSecret }
    });
    var orphanPackage = SecureTransferService.Read(orphanPath, exportKey);
    Check(orphanPackage.Credentials.TryGetValue(orphanId, out var preservedOrphan) &&
          preservedOrphan == orphanSecret,
          "B4 old encrypted backups preserve extra credential records for backward compatibility");

    var nullWorkspace = new PortableExport.PortablePackage
    {
        Config = new Configuration { Workspaces = new List<WorkspaceProfile> { null! } }
    };
    bool nullWorkspaceRejected = false;
    try { PortableImportValidation.Validate(nullWorkspace); }
    catch (InvalidDataException) { nullWorkspaceRejected = true; }
    Check(nullWorkspaceRejected, "B4 null workspace is rejected before publishing a vault snapshot");

    var workspaceId = Guid.NewGuid();
    var repeatedWorkspace = new PortableExport.PortablePackage
    {
        Config = new Configuration
        {
            Workspaces = new()
            {
                new WorkspaceProfile { Id = workspaceId, Name = "First" },
                new WorkspaceProfile { Id = workspaceId, Name = "Second" }
            }
        }
    };
    bool duplicateWorkspaceRejected = false;
    try { PortableImportValidation.Validate(repeatedWorkspace); }
    catch (InvalidDataException) { duplicateWorkspaceRejected = true; }
    Check(duplicateWorkspaceRejected, "B4 duplicate workspace ids cannot create ambiguous selection");

    var repeatedWorkspaceNames = new PortableExport.PortablePackage
    {
        Config = new Configuration
        {
            Workspaces = new()
            {
                new WorkspaceProfile { Id = Guid.NewGuid(), Name = "Morning" },
                new WorkspaceProfile { Id = Guid.NewGuid(), Name = " morning " }
            }
        }
    };
    bool duplicateWorkspaceNameRejected = false;
    try { PortableImportValidation.Validate(repeatedWorkspaceNames); }
    catch (InvalidDataException) { duplicateWorkspaceNameRejected = true; }
    Check(duplicateWorkspaceNameRejected,
        "B4 same-name workspaces are refused even with different IDs and case or whitespace");

    // Old backups can mention a deleted website in a workspace. The workspace
    // resolver intentionally ignores that reference, so imports must still work.
    var compatibleWorkspace = new PortableExport.PortablePackage
    {
        Config = new Configuration
        {
            Workflows = new() { new WebsiteWorkflow { Name = "Available", StartUrl = "https://example.test" } },
            Workspaces = new()
            {
                new WorkspaceProfile { Name = "Historical", WorkflowIds = new() { Guid.NewGuid() } }
            }
        }
    };
    PortableImportValidation.Validate(compatibleWorkspace);
    Check(compatibleWorkspace.Config.Workspaces.Single().Resolve(compatibleWorkspace.Config).Count() == 0,
        "B4 legacy workspace with removed website remains importable and resolves safely");

    var projected = ImportConfigurationProjection.ForThisDevice(
        new Configuration
        {
            KeePassDatabasePath = @"D:\OtherPC\passwords.kdbx",
            KeePassKeyFilePath = @"D:\OtherPC\key.key",
            FirstWebsiteGuidanceShown = true,
            Workflows =
            {
                new WebsiteWorkflow
                {
                    Id = Guid.NewGuid(),
                    Name = "KeePass site",
                    StartUrl = "https://example.test",
                    KeepassEntryPath = "Group/Entry"
                }
            }
        },
        config,
        "Ctrl+M");
    Check(projected.KeePassDatabasePath is null && projected.KeePassKeyFilePath is null &&
          projected.Workflows.Single().KeepassEntryPath is null &&
          projected.FirstWebsiteGuidanceShown,
        "B3 imported KeePass paths are cleared for this device");
}
finally
{
    current.Lock();
    SqliteConnection.ClearAllPools();
}
Console.WriteLine($"BACKUP_INTEGRITY_SUMMARY passed={passed} failed={failed}");
return failed == 0 ? 0 : 1;
