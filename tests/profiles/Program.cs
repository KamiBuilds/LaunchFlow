using System.IO.Compression;
using System.Security.Cryptography;
using LaunchFlow.Services;

if (args.Contains("--budget-only"))
{
    ExperimentalReadTests.RunBudget();
    return;
}

var root = Path.Combine(Path.GetTempPath(), "launchflow-seal-test-" + Guid.NewGuid().ToString("N"));
VaultDatabase.Current.DataDirectory = root;
try
{
    var profile = Path.Combine(ProfileVaultService.ProfilesRoot, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(profile);
    File.WriteAllText(Path.Combine(profile, "cookie.txt"), "fixture-only-cookie");
    var largeFixture = new byte[256 * 1024 + 17];
    RandomNumberGenerator.Fill(largeFixture);
    File.WriteAllBytes(Path.Combine(profile, "large.bin"), largeFixture);
    ProfileVaultService.SealAll();
    Assert(!Directory.Exists(profile), "successful sealing removes the plaintext profile");
    var firstArchive = Path.Combine(ProfileVaultService.SealedRoot, Path.GetFileName(profile) + ".profile.aes");
    var originalWrappedKey = File.ReadAllBytes(Path.Combine(root, "profile-key.dpapi"));
    AssertLegacyReaderCompatibility(firstArchive, largeFixture);
    ProfileVaultService.UnsealAll();
    Assert(File.ReadAllBytes(Path.Combine(profile, "large.bin")).SequenceEqual(largeFixture),
        "legacy archive restores a multi-block synthetic payload");
    Assert(File.Exists(Path.Combine(profile, "cookie.txt")), "profile decrypts after randomized DPAPI wrapping");
    Assert(File.ReadAllText(Path.Combine(profile, "cookie.txt")) == "fixture-only-cookie", "profile round trip retains exact synthetic cookie data");
    ProfileVaultService.SealAll();
    Assert(ProtectedData.LastUnprotected!.All(b => b == 0), "legacy write zeroes the unwrapped profile key");
    ProfileVaultService.UnsealAll();
    Assert(File.ReadAllBytes(Path.Combine(root, "profile-key.dpapi")).SequenceEqual(originalWrappedKey), "repeated legacy writes preserve the existing wrapped key exactly");
    Assert(CrashLog.Failures.Count == 0, "repeated profile lock/unlock reports no failures");
    var tamperedId = Guid.NewGuid().ToString("N");
    var tamperedPath = Path.Combine(ProfileVaultService.SealedRoot, tamperedId + ".profile.aes");
    var tampered = File.ReadAllBytes(firstArchive);
    tampered[24] ^= 0x01;
    File.WriteAllBytes(tamperedPath, tampered);
    var rejectedTamper = false;
    try { ProfileVaultService.UnsealAll(); }
    catch (AggregateException) { rejectedTamper = true; }
    Assert(rejectedTamper && !Directory.Exists(Path.Combine(ProfileVaultService.ProfilesRoot, tamperedId)),
        "tampered legacy authentication never publishes a browser profile");
    Assert(File.ReadAllBytes(tamperedPath).SequenceEqual(tampered), "tampered legacy authentication retains exact ciphertext");
    Assert(ProtectedData.LastUnprotected!.All(b => b == 0), "legacy authentication failure zeroes the unwrapped profile key");
    // Exercise production TryDecrypt rather than assuming AES-GCM's treatment
    // of output buffers after a rejected authentication tag.
    var testKey = ProtectedData.Unprotect(
        File.ReadAllBytes(Path.Combine(root, "profile-key.dpapi")), null, DataProtectionScope.CurrentUser);
    try
    {
        var decrypt = typeof(ProfileVaultService).GetMethod("TryDecrypt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new Exception("Production legacy profile decrypt boundary is missing");
        object?[] arguments = { tampered, testKey, Array.Empty<byte>() };
        var accepted = (bool)(decrypt.Invoke(null, arguments) ?? true);
        Assert(!accepted && arguments[2] is byte[] { Length: 0 },
            "rejected legacy GCM profile output is cleared and not returned to a caller");
    }
    finally { CryptographicOperations.ZeroMemory(testKey); }
    File.Delete(tamperedPath);
    var invalidId = Guid.NewGuid().ToString("N");
    using var zip = new MemoryStream();
    using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
    {
        using (var writer = new StreamWriter(archive.CreateEntry("cookie.txt").Open())) writer.Write("partial fixture");
        using (var writer = new StreamWriter(archive.CreateEntry("../escape.txt").Open())) writer.Write("must not escape");
    }
    var key = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(root, "profile-key.dpapi")), null, DataProtectionScope.CurrentUser);
    byte[] encrypted;
    try { encrypted = EncryptLegacy(zip.ToArray(), key); }
    finally { CryptographicOperations.ZeroMemory(key); }
    File.WriteAllBytes(Path.Combine(ProfileVaultService.SealedRoot, invalidId + ".profile.aes"), encrypted);
    var reportedFailure = false;
    try { ProfileVaultService.UnsealAll(); }
    catch (AggregateException) { reportedFailure = true; }
    Assert(reportedFailure, "failed profile restore is reported to the caller instead of silently claiming success");
    Assert(!Directory.Exists(Path.Combine(ProfileVaultService.ProfilesRoot, invalidId)), "failed profile extraction never publishes a partial open profile");
    Assert(!File.Exists(Path.Combine(ProfileVaultService.ProfilesRoot, "escape.txt")), "profile extraction blocks traversal");
    var invalidPath = Path.Combine(ProfileVaultService.SealedRoot, invalidId + ".profile.aes");
    Assert(File.ReadAllBytes(invalidPath).SequenceEqual(encrypted), "failed legacy extraction retains exact ciphertext");
    File.Delete(invalidPath);
    using (var independentZip = new MemoryStream())
    {
        using (var archive = new ZipArchive(independentZip, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("independent.txt").Open())) writer.Write("old-format fixture");
        var legacyKey = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(root, "profile-key.dpapi")), null, DataProtectionScope.CurrentUser);
        byte[] legacyFixture;
        try { legacyFixture = EncryptLegacy(independentZip.ToArray(), legacyKey); }
        finally { CryptographicOperations.ZeroMemory(legacyKey); }
        var legacyPath = Path.Combine(ProfileVaultService.SealedRoot, "independent-legacy.profile.aes");
        File.WriteAllBytes(legacyPath, legacyFixture);
        ProfileVaultService.UnsealAll();
        Assert(File.ReadAllText(Path.Combine(ProfileVaultService.ProfilesRoot, "independent-legacy", "independent.txt")) == "old-format fixture",
            "independently constructed legacy AES-GCM fixture restores");
        Assert(File.ReadAllBytes(legacyPath).SequenceEqual(legacyFixture), "legacy read does not migrate its ciphertext");
        Assert(ProtectedData.LastUnprotected!.All(b => b == 0), "legacy successful read zeroes the unwrapped profile key");
    }
    ExperimentalReadTests.Run();
    var originalCipher = File.ReadAllBytes(Path.Combine(ProfileVaultService.SealedRoot, Path.GetFileName(profile) + ".profile.aes"));
    File.Delete(Path.Combine(root, "profile-key.dpapi"));
    var missingKeyReported = false;
    try { ProfileVaultService.SealAll(); }
    catch (AggregateException) { missingKeyReported = true; }
    Assert(missingKeyReported, "missing legacy profile key is reported instead of replacing existing ciphertext");
    Assert(Directory.Exists(profile), "failed sealing retains the current plaintext profile");
    Assert(originalCipher.SequenceEqual(File.ReadAllBytes(Path.Combine(ProfileVaultService.SealedRoot, Path.GetFileName(profile) + ".profile.aes"))), "legacy ciphertext is never overwritten without its key");
    ExperimentalReadTests.RunBudget();
    RunVaultResetHonestyTests();
    Console.WriteLine("Profile tests passed (simulated DPAPI contract; native Windows gate remains).");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

static void RunVaultResetHonestyTests()
{
    var resetRoot = Path.Combine(Path.GetTempPath(), "launchflow-reset-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(resetRoot);
    var previous = VaultDatabase.Current.DataDirectory;
    VaultDatabase.Current.DataDirectory = resetRoot;
    WindowsHelloService.DisableCalls = 0;
    try
    {
        File.WriteAllText(VaultDatabase.Current.DatabasePath, "fixture-db");
        var plainMarker = Path.Combine(ProfileVaultService.ProfilesRoot, "plain-marker");
        var sealedMarker = Path.Combine(ProfileVaultService.SealedRoot, "sealed-marker");
        Directory.CreateDirectory(plainMarker);
        Directory.CreateDirectory(sealedMarker);
        File.WriteAllText(Path.Combine(plainMarker, "cookie.txt"), "fixture");
        File.WriteAllText(Path.Combine(sealedMarker, "blob.bin"), "fixture");

        var ok = VaultResetService.ResetLocalVault(wipeProfiles: true);
        Assert(ok.Succeeded && !File.Exists(VaultDatabase.Current.DatabasePath),
            "healthy reset removes the vault database");
        Assert(!Directory.Exists(ProfileVaultService.ProfilesRoot) && !Directory.Exists(ProfileVaultService.SealedRoot),
            "healthy reset removes plaintext and sealed profile roots");
        Assert(WindowsHelloService.DisableCalls >= 1, "reset disables Windows Hello unlock material");
        var helloAfterHealthy = WindowsHelloService.DisableCalls;

        // Use deterministic owned fault injection on every platform. An open
        // FileShare.None handle is not a deletion-denial guarantee on Unix.
        var unrelated = Path.Combine(resetRoot, "unrelated-backup.txt");
        File.WriteAllText(unrelated, "unchanged");
        File.WriteAllText(VaultDatabase.Current.DatabasePath, "fixture-db");
        Directory.CreateDirectory(plainMarker);
        File.WriteAllText(Path.Combine(plainMarker, "cookie.txt"), "retained");
        Directory.CreateDirectory(sealedMarker);
        File.WriteAllText(Path.Combine(sealedMarker, "blob.bin"), "retained-sealed");
        AggregateException? dbDenied = null;
        var profileWipeCalled = false;
        try
        {
            VaultResetService.ResetLocalVault(true,
                path =>
                {
                    if (path == VaultDatabase.Current.DatabasePath)
                        throw new UnauthorizedAccessException("Synthetic database denial");
                    File.Delete(path);
                },
                () => { profileWipeCalled = true; return ProfileVaultService.WipeAllProfilesStrict(); });
        }
        catch (AggregateException ex) { dbDenied = ex; }
        Assert(dbDenied is { InnerExceptions.Count: 1 } &&
            dbDenied.InnerExceptions[0] is IOException { InnerException: UnauthorizedAccessException },
            "portable database denial reports structured original filesystem fault");
        Assert(!profileWipeCalled && File.Exists(VaultDatabase.Current.DatabasePath) &&
            Directory.Exists(plainMarker) && Directory.Exists(sealedMarker) &&
            WindowsHelloService.DisableCalls == helloAfterHealthy,
            "database denial preserves existing profiles and Hello authorization material");

        // Real native Windows FileShare.None remains a separate physical check.
        if (OperatingSystem.IsWindows())
        {
            using var held = new FileStream(VaultDatabase.Current.DatabasePath,
                FileMode.Open, FileAccess.Read, FileShare.None);
            AggregateException? nativeDenied = null;
            try { VaultResetService.ResetLocalVault(true); }
            catch (AggregateException ex) { nativeDenied = ex; }
            Assert(nativeDenied is not null && File.Exists(VaultDatabase.Current.DatabasePath) &&
                Directory.Exists(plainMarker) && Directory.Exists(sealedMarker),
                "native Windows database sharing denial retains originals");
        }

        // These failure collaborators route through production cleanup. A
        // denied root is never deleted; other roots are still attempted.
        AggregateException? plainDenied = null;
        try
        {
            VaultResetService.ResetLocalVault(true, File.Delete,
                () => ProfileVaultService.WipeAllProfilesStrict(path =>
                {
                    if (path == ProfileVaultService.ProfilesRoot)
                        throw new IOException("Synthetic plaintext profile denial");
                    Directory.Delete(path, true);
                }));
        }
        catch (AggregateException ex) { plainDenied = ex; }
        Assert(plainDenied is { InnerExceptions.Count: 1 } &&
            plainDenied.InnerExceptions[0] is IOException { InnerException: IOException },
            "plaintext profile denial is reported with original nested cause");
        Assert(!File.Exists(VaultDatabase.Current.DatabasePath) &&
            Directory.Exists(plainMarker) && !Directory.Exists(ProfileVaultService.SealedRoot),
            "partial plaintext cleanup leaves only the denied root");

        File.WriteAllText(VaultDatabase.Current.DatabasePath, "fixture-db");
        Directory.CreateDirectory(sealedMarker);
        File.WriteAllText(Path.Combine(sealedMarker, "blob.bin"), "retained-sealed");
        AggregateException? sealedDenied = null;
        try
        {
            VaultResetService.ResetLocalVault(true, File.Delete,
                () => ProfileVaultService.WipeAllProfilesStrict(path =>
                {
                    if (path == ProfileVaultService.SealedRoot)
                        throw new IOException("Synthetic sealed profile denial");
                    Directory.Delete(path, true);
                }));
        }
        catch (AggregateException ex) { sealedDenied = ex; }
        Assert(sealedDenied is { InnerExceptions.Count: 1 } &&
            sealedDenied.InnerExceptions[0] is IOException { InnerException: IOException },
            "sealed profile denial is reported with the nested cause");
        Assert(!File.Exists(VaultDatabase.Current.DatabasePath) &&
            !Directory.Exists(ProfileVaultService.ProfilesRoot) && Directory.Exists(sealedMarker),
            "partial sealed cleanup retains encrypted profile data");

        File.WriteAllText(VaultDatabase.Current.DatabasePath, "fixture-db");
        Directory.CreateDirectory(plainMarker);
        File.WriteAllText(Path.Combine(plainMarker, "cookie.txt"), "fixture");
        AggregateException? bothDenied = null;
        try
        {
            VaultResetService.ResetLocalVault(true, File.Delete,
                () => ProfileVaultService.WipeAllProfilesStrict(_ =>
                    throw new IOException("Synthetic combined roots denial")));
        }
        catch (AggregateException ex) { bothDenied = ex; }
        Assert(bothDenied is { InnerExceptions.Count: 2 } &&
            bothDenied.InnerExceptions.All(ex => ex is IOException { InnerException: IOException }),
            "independent plaintext and sealed failures remain in the result graph");
        Assert(Directory.Exists(plainMarker) && Directory.Exists(sealedMarker),
            "combined denial retains both failed profile roots");

        File.WriteAllText(VaultDatabase.Current.DatabasePath, "fixture-db");
        var preserved = VaultResetService.ResetLocalVault(wipeProfiles: false);
        Assert(preserved.Succeeded && !File.Exists(VaultDatabase.Current.DatabasePath),
            "wipeProfiles=false resets only the database");
        Assert(Directory.Exists(plainMarker) && Directory.Exists(sealedMarker),
            "wipeProfiles=false preserves plaintext and sealed browser profiles");
        Assert(File.ReadAllText(unrelated) == "unchanged",
            "vault reset never modifies unrelated files/backups");

        // Retry remaining cleanup after a partial failure.
        Directory.CreateDirectory(plainMarker);
        File.WriteAllText(Path.Combine(plainMarker, "cookie.txt"), "retry");
        var retry = VaultResetService.ResetLocalVault(wipeProfiles: true);
        Assert(retry.Succeeded && !Directory.Exists(ProfileVaultService.ProfilesRoot),
            "retry after partial reset finishes remaining profile cleanup");
        var repeat = VaultResetService.ResetLocalVault(wipeProfiles: true);
        Assert(repeat.Succeeded && repeat.DatabaseRemoved && repeat.ProfilesRemoved,
            "missing vault and profile roots can be reset repeatedly");
        Assert(File.ReadAllText(unrelated) == "unchanged",
            "repeat reset leaves unrelated backup intact");
    }
    finally
    {
        VaultDatabase.Current.DataDirectory = previous;
        if (Directory.Exists(resetRoot))
        {
            try { Directory.Delete(resetRoot, true); }
            catch { /* locked markers may need a delayed cleanup */ }
        }
    }
}

static byte[] EncryptLegacy(byte[] zip, byte[] key)
{
    var output = new byte[zip.Length + 28];
    RandomNumberGenerator.Fill(output.AsSpan(0, 12));
    using var aes = new AesGcm(key, 16);
    aes.Encrypt(output.AsSpan(0, 12), zip, output.AsSpan(12, zip.Length), output.AsSpan(12 + zip.Length, 16));
    return output;
}

// Independent old-reader contract from origin/both 377dbbf: nonce[12], ciphertext, tag[16], no AAD.
// Do not call production Encrypt/TryDecrypt: this must catch an incompatible default writer.
static void AssertLegacyReaderCompatibility(string path, byte[] expected)
{
    var key = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(VaultDatabase.Current.DataDirectory,
        "profile-key.dpapi")), null, DataProtectionScope.CurrentUser);
    var bytes = File.ReadAllBytes(path);
    var zipBytes = new byte[bytes.Length - 28];
    try
    {
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(12, bytes.Length - 28),
            bytes.AsSpan(bytes.Length - 16), zipBytes);
        using var zip = new ZipArchive(new MemoryStream(zipBytes, writable: false), ZipArchiveMode.Read);
        using var content = new MemoryStream();
        zip.GetEntry("large.bin")!.Open().CopyTo(content);
        Assert(content.ToArray().SequenceEqual(expected), "default write decrypts with exact origin/both legacy AES-GCM layout and persisted key");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(zipBytes);
    }
}

static void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
}
