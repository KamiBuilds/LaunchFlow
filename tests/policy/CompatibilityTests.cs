using LaunchFlow.Models;
using LaunchFlow.Services;

internal static class CompatibilityTests
{
    internal static int Run(string mode)
    {
        var root = Environment.GetEnvironmentVariable("LAUNCHFLOW_POLICY_TEST_ROOT")
            ?? throw new InvalidOperationException("Isolated compatibility root required.");
        var db = VaultDatabase.Current;
        VaultTests.Check(Path.GetFullPath(db.DatabasePath).StartsWith(
            Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "Compatibility harness must use isolated synthetic vault.");

        var id = Guid.Parse("5184d8af-533b-4b24-9a12-4f3e21980993");
        var second = Guid.Parse("37eeb731-a4c4-4590-b407-53e80ac88f28");
        var secret = new SiteCredential("FixtureCompatUser", "FixtureOnlyCompatSecret73");
        const string password = "FixtureOnly-Compat-73!";
        var config = new Configuration
        {
            Workflows = new() { new() { Id = id, Name = "Fixture compatibility", Enabled = true } },
            GlobalShortcut = "Ctrl+M",
            UiLanguage = "auto"
        };
        if (mode == "--compat-create")
        {
#if !LEGACY_COMPAT
            throw new InvalidOperationException("The legacy producer must use pinned historical sources.");
#else
            VaultTests.Check(!db.Exists, "Legacy producer must begin with a new fixture.");
            db.Initialize(password, config, new() { [id] = secret });
            SecureTransferService.Export(Path.Combine(root, "baseline.launchflow"),
                password, config, new CredentialVault(), true);
#endif
        }
        else if (mode == "--compat-read")
        {
            VaultTests.Check(db.Unlock(password),
                "Retained legacy reader could not authorize its original recovery copy.");
            VaultTests.Check(db.ReadCredential(id) == secret &&
                db.LoadConfiguration().Workflows.Single().Id == id,
                "Original historical credential/identity changed.");
            var portable = SecureTransferService.Read(
                Path.Combine(root, "baseline.launchflow"), password);
            VaultTests.Check(portable.Credentials[id] == secret,
                "Historical portable backup did not retain credential.");
        }
        else if (mode == "--compat-deny")
        {
#if !LEGACY_COMPAT
            throw new InvalidOperationException("Only the actual retained reader may assert old-reader denial.");
#else
            // The actual historical reader throws for missing legacy metadata
            // rather than returning false. Either outcome is a valid refusal,
            // provided it never publishes an unlocked session.
            bool denied;
            try { denied = !db.Unlock(password); }
            catch (Exception ex) when (ex is InvalidOperationException or
                Microsoft.Data.Sqlite.SqliteException) { denied = true; }
            VaultTests.Check(denied && !db.IsUnlocked,
                "Old reader unexpectedly authorized strict v3 storage.");
            bool rejected = false;
            try { db.ChangeMasterPassword(password, "UpdatedSyntheticMaster73!"); }
            catch { rejected = true; }
            VaultTests.Check(rejected && !db.IsUnlocked,
                "Old reader unexpectedly changed the strict vault master key.");
#endif
        }
        else if (mode == "--compat-update")
        {
#if LEGACY_COMPAT
            throw new InvalidOperationException("Only the strict writer may perform a legacy upgrade.");
#else
            VaultTests.Check(db.InspectStorage() == VaultStorageState.LegacyUpgradeRequired,
                "Legacy vault was not classified as requiring explicit upgrade.");
            VaultTests.Check(!db.Unlock(password) && !db.IsUnlocked,
                "Legacy vault was admitted without an explicit conversion.");
            var backup = db.UpgradeLegacyVault(password);
            VaultTests.Check(File.Exists(backup) && db.IsUnlocked,
                "Verified legacy recovery snapshot was not produced.");
            var initial = db.LoadConfiguration();
            VaultTests.Check(!initial.Workflows.Single().Enabled &&
                db.ReadCredential(id) == secret, "Upgrade lost data or enabled automatic login.");
            var changed = db.LoadConfiguration();
            changed.Workflows.Single().Name = "Updated by current writer";
            changed.UiLanguage = "nl";
            changed.GlobalShortcut = "Ctrl+Shift+M";
            changed.Workspaces.Add(new WorkspaceProfile
            {
                Name = "Compatibility workspace",
                WorkflowIds = new() { id }
            });
            var generation = db.SessionGeneration;
            new ConfigStore().SaveDurable(changed, generation);
            db.SaveCredential(second, secret);
            SecureTransferService.Export(Path.Combine(root, "updated.launchflow"),
                password, changed, new CredentialVault(), true);
#endif
        }
        else if (mode == "--compat-verify")
        {
#if LEGACY_COMPAT
            throw new InvalidOperationException("Only the new strict reader can verify converted data.");
#else
            VaultTests.Check(db.Unlock(password), "Strict writer cannot cold-open converted data.");
            var updated = db.LoadConfiguration();
            VaultTests.Check(db.ReadCredential(id) == secret &&
                db.ReadCredential(second) == secret &&
                updated.Workflows.Single().Id == id &&
                updated.Workflows.Single().Name == "Updated by current writer" &&
                updated.UiLanguage == "nl" &&
                updated.GlobalShortcut == "Ctrl+Shift+M" &&
                !updated.Workflows.Single().Enabled &&
                updated.Workspaces.Single().Name == "Compatibility workspace",
                "Strict v3 conversion/write/read did not retain logical state.");
            var portable = SecureTransferService.Read(
                Path.Combine(root, "updated.launchflow"), password);
            VaultTests.Check(portable.Credentials[id] == secret &&
                portable.Credentials[second] == secret,
                "Existing LFLW1 encrypted export changed during upgrade.");
#endif
        }
        else throw new InvalidOperationException("Unknown compatibility mode.");

        db.Lock();
        Console.WriteLine("PASS: " + mode + " pinned legacy and strict v3 compatibility contract.");
        return 0;
    }
}
