using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using LaunchFlow.Services;

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
    Assert(File.ReadAllBytes(firstArchive).AsSpan(0, 5).SequenceEqual("LFPS2"u8),
        "new profile archive uses bounded streaming format");
    ProfileVaultService.UnsealAll();
    Assert(File.ReadAllBytes(Path.Combine(profile, "large.bin")).SequenceEqual(largeFixture),
        "chunked archive restores data across multiple encryption blocks");
    Assert(File.Exists(Path.Combine(profile, "cookie.txt")), "profile decrypts after randomized DPAPI wrapping");
    Assert(File.ReadAllText(Path.Combine(profile, "cookie.txt")) == "fixture-only-cookie", "profile round trip retains exact synthetic cookie data");
    ProfileVaultService.SealAll();
    ProfileVaultService.UnsealAll();
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
        "tampered authenticated chunks never publish a browser profile");
    File.Delete(tamperedPath);
    var invalidId = Guid.NewGuid().ToString("N");
    using var zip = new MemoryStream();
    using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
    {
        using (var writer = new StreamWriter(archive.CreateEntry("cookie.txt").Open())) writer.Write("partial fixture");
        using (var writer = new StreamWriter(archive.CreateEntry("../escape.txt").Open())) writer.Write("must not escape");
    }
    var key = (byte[])typeof(ProfileVaultService).GetMethod("ProfileKey", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
    var encrypted = (byte[])typeof(ProfileVaultService).GetMethod("Encrypt", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { zip.ToArray(), key })!;
    CryptographicOperations.ZeroMemory(key);
    File.WriteAllBytes(Path.Combine(ProfileVaultService.SealedRoot, invalidId + ".profile.aes"), encrypted);
    var reportedFailure = false;
    try { ProfileVaultService.UnsealAll(); }
    catch (AggregateException) { reportedFailure = true; }
    Assert(reportedFailure, "failed profile restore is reported to the caller instead of silently claiming success");
    Assert(!Directory.Exists(Path.Combine(ProfileVaultService.ProfilesRoot, invalidId)), "failed profile extraction never publishes a partial open profile");
    Assert(!File.Exists(Path.Combine(ProfileVaultService.ProfilesRoot, "escape.txt")), "profile extraction blocks traversal");
    var originalCipher = File.ReadAllBytes(Path.Combine(ProfileVaultService.SealedRoot, Path.GetFileName(profile) + ".profile.aes"));
    File.Delete(Path.Combine(root, "profile-key.dpapi"));
    var missingKeyReported = false;
    try { ProfileVaultService.SealAll(); }
    catch (AggregateException) { missingKeyReported = true; }
    Assert(missingKeyReported, "missing legacy profile key is reported instead of replacing existing ciphertext");
    Assert(Directory.Exists(profile), "failed sealing retains the current plaintext profile");
    Assert(originalCipher.SequenceEqual(File.ReadAllBytes(Path.Combine(ProfileVaultService.SealedRoot, Path.GetFileName(profile) + ".profile.aes"))), "legacy ciphertext is never overwritten without its key");
    Console.WriteLine("Profile tests passed (simulated DPAPI contract; native Windows gate remains).");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

static void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
}
