using LaunchFlow.Models;
using LaunchFlow.Services;

internal static class CompatibilityTests
{
    internal static int Run(string mode)
    {
        var root = Environment.GetEnvironmentVariable("LAUNCHFLOW_POLICY_TEST_ROOT") ?? throw new InvalidOperationException("Isolated root required.");
        var db = VaultDatabase.Current;
        VaultTests.Check(Path.GetFullPath(db.DatabasePath).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        var id = Guid.Parse("5184d8af-533b-4b24-9a12-4f3e21980993");
        var second = Guid.Parse("37eeb731-a4c4-4590-b407-53e80ac88f28");
        var secret = new SiteCredential("FixtureCompatUser", "FixtureOnlyCompatSecret73");
        const string password = "FixtureOnly-Compat-73!";
        var config = new Configuration { Workflows = new() { new() { Id = id, Name = "Fixture compatibility", Enabled = false } } };
        if (mode == "--compat-create")
        {
            VaultTests.Check(!db.Exists);
            db.Initialize(password, config, new() { [id] = secret });
            SecureTransferService.Export(Path.Combine(root, "baseline.launchflow"), password, config, new(), true);
        }
        else
        {
            VaultTests.Check(db.Unlock(password));
            VaultTests.Check(db.ReadCredential(id) == secret && db.LoadConfiguration().Workflows.Single().Id == id);
            VaultTests.Check(SecureTransferService.Read(Path.Combine(root, "baseline.launchflow"), password).Credentials[id] == secret);
            if (mode == "--compat-update")
            {
                db.SaveCredential(second, secret);
                SecureTransferService.Export(Path.Combine(root, "updated.launchflow"), password, config, new(), true);
            }
            else if (mode == "--compat-read")
            {
                VaultTests.Check(db.ReadCredential(second) == secret);
                VaultTests.Check(SecureTransferService.Read(Path.Combine(root, "updated.launchflow"), password).Credentials[second] == secret);
            }
            else throw new InvalidOperationException("Unknown compatibility mode.");
        }
        db.Lock();
        Console.WriteLine("PASS: " + mode + " real encrypted SQLite and LFLW1 portable package");
        return 0;
    }
}
