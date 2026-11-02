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
            Workflows = new() { new() { Id = id, Name = "Fixture compatibility", Enabled = false } },
            GlobalShortcut = "Ctrl+M",
            UiLanguage = "auto"
        };

        if (mode == "--compat-create")
        {
            VaultTests.Check(!db.Exists, "Legacy producer must begin with a new synthetic vault.");
            db.Initialize(password, config, new() { [id] = secret });
            SecureTransferService.Export(Path.Combine(root, "baseline.launchflow"),
                password, config, new CredentialVault(), true);
        }
        else
        {
            VaultTests.Check(db.Unlock(password), "Cannot unlock baseline/updated synthetic vault.");
            var initial = db.LoadConfiguration();
            VaultTests.Check(db.ReadCredential(id) == secret &&
                initial.Workflows.Single().Id == id, "Baseline identity/credential mismatch.");
            var baselinePackage = SecureTransferService.Read(
                Path.Combine(root, "baseline.launchflow"), password);
            VaultTests.Check(baselinePackage.Credentials[id] == secret &&
                baselinePackage.Config.Workflows.Single().Id == id,
                "Baseline portable export mismatch.");
            if (mode == "--compat-update")
            {
#if LEGACY_COMPAT
                throw new InvalidOperationException("The current writer must not run with legacy sources.");
#else
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
                var committed = db.LoadConfiguration();
                VaultTests.Check(committed.Workflows.Single().Name == "Updated by current writer" &&
                    committed.UiLanguage == "nl" &&
                    committed.Workspaces.Single().WorkflowIds.SequenceEqual(new[] { id }),
                    "Current durable configuration write did not commit.");
                SecureTransferService.Export(Path.Combine(root, "updated.launchflow"),
                    password, committed, new CredentialVault(), true);
#endif
            }
            else if (mode == "--compat-read")
            {
                var updated = db.LoadConfiguration();
                VaultTests.Check(db.ReadCredential(second) == secret,
                    "Legacy reader could not decrypt current credential update.");
                VaultTests.Check(updated.Workflows.Single().Id == id &&
                    updated.Workflows.Single().Name == "Updated by current writer" &&
                    updated.UiLanguage == "nl" &&
                    updated.GlobalShortcut == "Ctrl+Shift+M" &&
                    updated.Workspaces.Single().Name == "Compatibility workspace" &&
                    updated.Workspaces.Single().WorkflowIds.SequenceEqual(new[] { id }),
                    "Legacy reader could not read current durable configuration.");
                var updatedPackage = SecureTransferService.Read(
                    Path.Combine(root, "updated.launchflow"), password);
                VaultTests.Check(updatedPackage.Credentials[second] == secret &&
                    updatedPackage.Config.Workflows.Single().Name == "Updated by current writer" &&
                    updatedPackage.Config.Workspaces.Single().WorkflowIds.SequenceEqual(new[] { id }),
                    "Legacy reader could not read current portable export.");
            }
            else throw new InvalidOperationException("Unknown compatibility mode.");
        }

        db.Lock();
        Console.WriteLine("PASS: " + mode +
            " actual encrypted SQLite, durable configuration and LFLW1 export.");
        return 0;
    }
}
