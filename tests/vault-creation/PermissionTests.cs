using LaunchFlow.Services;

internal static partial class Program
{
    // Run in a Linux child without DAC override/read-search capabilities, not
    // by changing the parent user or any real vault permissions.
    private static void SetPermissions(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        File.SetUnixFileMode(path, mode);
    }

    private static void RunPermissionTests()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        Test("actual inaccessible destination is incomplete and never replaced", f =>
        {
            var bytes = new byte[] { 81, 92, 73 }; File.WriteAllBytes(f.Path, bytes);
            SetPermissions(f.Path, UnixFileMode.None);
            try
            {
                Check(Refused(() => File.ReadAllBytes(f.Path)) is UnauthorizedAccessException, "Fixture did not actually deny reads.");
                var db = new VaultDatabase(f.Path);
                Check(db.InspectStorage() == VaultStorageState.IncompleteOrUnsupported, "Unreadable file became fresh setup.");
                Refused(() => db.Initialize(Master(), new()));
                Check(!db.IsUnlocked, "Unreadable destination published authority.");
            }
            finally { SetPermissions(f.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            Check(File.ReadAllBytes(f.Path).SequenceEqual(bytes), "Unreadable destination was modified.");
        });
        Test("actual inaccessible data directory is incomplete not absent", f =>
        {
            SetPermissions(f.Root, UnixFileMode.None);
            try
            {
                Check(Refused(() => Directory.GetFileSystemEntries(f.Root)) is UnauthorizedAccessException, "Fixture did not actually deny enumeration.");
                var db = new VaultDatabase(f.Path);
                Check(db.InspectStorage() == VaultStorageState.IncompleteOrUnsupported, "Unreadable directory became fresh setup.");
                Refused(() => db.Initialize(Master(), new()));
            }
            finally { SetPermissions(f.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            Check(!Directory.EnumerateFileSystemEntries(f.Root).Any(), "Inaccessible creation left residue.");
        });
        Test("actual read-only data directory failure leaves no destination then retry succeeds", f =>
        {
            SetPermissions(f.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var db = new VaultDatabase(f.Path);
            try
            {
                Check(Refused(() => File.WriteAllBytes(f.Path, new byte[] { 1 })) is UnauthorizedAccessException, "Fixture did not actually deny writes.");
                Refused(() => db.Initialize(Master(), new()));
                Check(!db.IsUnlocked && !File.Exists(f.Path), "Read-only creation published a vault.");
            }
            finally { SetPermissions(f.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            Check(!Directory.EnumerateFileSystemEntries(f.Root).Any(), "Read-only failure left residue.");
            var owner = Master(); db.Initialize(owner, new() { UiLanguage = "synthetic" }); db.Lock(); ColdOwner(f.Path, owner);
        });
        Test("actual rollback cleanup denial preserves primary, secondary and retained candidate", f =>
        {
            var db = new VaultDatabase(f.Path); string? stage = null;
            db.TestCreationOperation = op =>
            {
                if (op != "VaultCreatePersistCredentials") return;
                stage = Directory.GetDirectories(f.Root, ".vault.db.create-*").Single();
                SetPermissions(stage, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            };
            try
            {
                var failure = Refused(() => db.Initialize(Master(), new(), new() { [Guid.Empty] = new("fixture@example.invalid", "synthetic") }));
                Check(stage is not null && Graph(failure).Any(e => e is InvalidDataException) && Graph(failure).Any(e => e is UnauthorizedAccessException), "Real primary or actual filesystem cleanup failure was lost.");
                Check(Outcome(failure) == "NotPublished" && Retained(failure) == stage, "Retained failed creation outcome was not explicit.");
                Check(!db.IsUnlocked && !File.Exists(f.Path) && Directory.Exists(stage), "Cleanup denial published or destroyed state.");
                Refused(() => new VaultDatabase(f.Path).Initialize(Master(), new()));
            }
            finally { if (stage is not null) SetPermissions(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        });

        Test("actual read-only owned main file remains inspectable and unchanged", f =>
        {
            var owner = Master(); var db = new VaultDatabase(f.Path);
            db.Initialize(owner, new() { UiLanguage = "synthetic" }); db.Lock();
            var before = File.ReadAllBytes(f.Path);
            SetPermissions(f.Path, UnixFileMode.UserRead);
            try
            {
                Check(db.InspectStorage() == VaultStorageState.PresentNeedsAuthentication, "Read-only recognized vault was misclassified.");
                Refused(() => db.Initialize(Master(), new()));
                Check(File.ReadAllBytes(f.Path).SequenceEqual(before), "Read-only inspection changed main bytes.");
            }
            finally { SetPermissions(f.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            ColdOwner(f.Path, owner);
        });
    }
}
