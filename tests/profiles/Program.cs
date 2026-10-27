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
    Console.WriteLine("Profile tests passed (simulated DPAPI contract; native Windows gate remains).");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
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
