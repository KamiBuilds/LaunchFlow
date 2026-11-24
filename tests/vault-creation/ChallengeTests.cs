using LaunchFlow.Services;

internal static partial class Program
{
    private static void RunChallengeTests()
    {
        RunStorageCharacterizations();

        foreach (var kind in new[] { "PasswordUtf8", "CloneConfig" })
        foreach (var faulted in new[] { false, true })
            Test("sensitive " + kind + " is zeroed after " + (faulted ? "modeled failure" : "success"), f =>
            {
                var observed = new List<byte[]>(); var fault = new SyntheticFault();
                var db = new VaultDatabase(f.Path);
                try
                {
                    VaultDatabase.TestSensitiveBuffer = (name, bytes) =>
                    {
                        if (name != kind) return;
                        observed.Add(bytes);
                        if (faulted) throw fault;
                    };
                    if (faulted)
                        Check(ReferenceEquals(Refused(() => db.Initialize(Master(), new())), fault), "Buffer failure seam did not run.");
                    else db.Initialize(Master(), new());
                    Check(observed.Count > 0, "Sensitive buffer seam did not run.");
                    Check(observed.All(bytes => bytes.All(b => b == 0)), "Owned sensitive byte buffer was not zeroed.");
                }
                finally { VaultDatabase.TestSensitiveBuffer = null; db.Lock(); }
            });

        foreach (var kind in new[] { "lease", "empty-directory" })
            Test("inert retained " + kind + " is preserved without blocking ordinary creation", f =>
            {
                var inert = System.IO.Path.Combine(f.Root, ".vault.db.create-inert" + (kind == "lease" ? ".lease" : ""));
                if (kind == "lease") File.WriteAllBytes(inert, Array.Empty<byte>());
                else Directory.CreateDirectory(inert);
                var db = new VaultDatabase(f.Path);
                Check(db.InspectStorage() == VaultStorageState.Absent, "Inert stage made fresh storage unavailable.");
                var owner = Master(); db.Initialize(owner, new() { UiLanguage = "synthetic" }); db.Lock();
                Check(kind == "lease" ? File.Exists(inert) : Directory.Exists(inert), "Unknown inert reservation was removed.");
                ColdOwner(f.Path, owner);
            });
    }
}
