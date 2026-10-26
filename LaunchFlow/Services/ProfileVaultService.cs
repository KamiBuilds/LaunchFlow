using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace LaunchFlow.Services;

/// <summary>
/// Seals managed browser profiles for the current Windows account and restores complete snapshots.
/// This is separate from vault-password encryption; failures retain source files and are reported.
/// It is not containment against other processes running as the same Windows user.
/// </summary>
public static class ProfileVaultService
{
    public static string ProfilesRoot =>
        Path.Combine(VaultDatabase.Current.DataDirectory, "profiles-v2");

    public static string SealedRoot =>
        Path.Combine(VaultDatabase.Current.DataDirectory, "profiles-sealed");

    public static void SealAll()
    {
        if (!Directory.Exists(ProfilesRoot)) return;
        Directory.CreateDirectory(SealedRoot);

        var failures = new List<Exception>();
        foreach (var dir in Directory.GetDirectories(ProfilesRoot))
        {
            try
            {
                SealDirectory(dir);
            }
            catch (Exception ex)
            {
                CrashLog.Write("ProfileSeal", ex);
                failures.Add(ex);
            }
        }
        if (failures.Count > 0)
            throw new AggregateException("Some browser profiles could not be sealed. Their plaintext folders were retained.", failures);
    }

    public static void UnsealAll()
    {
        if (!Directory.Exists(SealedRoot)) return;
        Directory.CreateDirectory(ProfilesRoot);

        var failures = new List<Exception>();
        foreach (var file in Directory.GetFiles(SealedRoot, "*.profile.aes"))
        {
            try
            {
                UnsealFile(file);
            }
            catch (Exception ex)
            {
                CrashLog.Write("ProfileUnseal", ex);
                failures.Add(ex);
            }
        }
        if (failures.Count > 0)
            throw new AggregateException("Some browser profiles could not be restored. Their encrypted files were retained.", failures);
    }

    public static void WipeAllProfiles()
    {
        TryDeleteDir(ProfilesRoot);
        TryDeleteDir(SealedRoot);
    }

    private static void SealDirectory(string directory)
    {
        var id = Path.GetFileName(directory);
        var sealedPath = Path.Combine(SealedRoot, id + ".profile.aes");
        var temp = sealedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var key = ProfileKey();
        using var zip = new MemoryStream();
        byte[] plain = Array.Empty<byte>();
        try
        {
            // Do not create a second plaintext copy in the shared temporary directory.
            ZipFile.CreateFromDirectory(directory, zip, CompressionLevel.Optimal, includeBaseDirectory: false);
            plain = zip.ToArray();
            var sealedBytes = Encrypt(plain, key);
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(sealedBytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temp, sealedPath, overwrite: true);
            Directory.Delete(directory, recursive: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
            if (zip.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan());
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void UnsealFile(string sealedPath)
    {
        var id = Path.GetFileName(sealedPath).Replace(".profile.aes", "", StringComparison.OrdinalIgnoreCase);
        var target = Path.Combine(ProfilesRoot, id);
        if (Directory.Exists(target)) return; // already open

        var sealedBytes = File.ReadAllBytes(sealedPath);
        var key = ProfileKey();
        try
        {
            if (!TryDecrypt(sealedBytes, key, out var zipBytes))
                throw new CryptographicException("Profile package could not be decrypted.");
            var staging = Path.Combine(ProfilesRoot, ".restore-" + Guid.NewGuid().ToString("N"));
            try
            {
                // Publish only a complete extraction; a failed restore must remain retryable.
                Directory.CreateDirectory(staging);
                using var archive = new ZipArchive(new MemoryStream(zipBytes, writable: false), ZipArchiveMode.Read);
                archive.ExtractToDirectory(staging, overwriteFiles: false);
                Directory.Move(staging, target);
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                CryptographicOperations.ZeroMemory(zipBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Profiles use a persisted random key wrapped for this Windows account, independent of the vault password.</summary>
    private static byte[] ProfileKey()
    {
        var path = Path.Combine(VaultDatabase.Current.DataDirectory, "profile-key.dpapi");
        if (File.Exists(path))
        {
            var key = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            if (key.Length == 32) return key;
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException("The browser-profile key is invalid. Existing files were not changed.");
        }
        // The old implementation hashed randomized DPAPI ciphertext and never saved it. It cannot be reconstructed.
        if (Directory.Exists(SealedRoot) && Directory.EnumerateFiles(SealedRoot, "*.profile.aes").Any())
            throw new CryptographicException("Existing sealed browser profiles have no recoverable key. Their files were not changed.");

        Directory.CreateDirectory(VaultDatabase.Current.DataDirectory);
        var created = RandomNumberGenerator.GetBytes(32);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var wrapped = ProtectedData.Protect(created, null, DataProtectionScope.CurrentUser);
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(wrapped);
                output.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: false);
            return created;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(created);
            throw;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        var output = new byte[12 + ciphertext.Length + 16];
        Buffer.BlockCopy(nonce, 0, output, 0, 12);
        Buffer.BlockCopy(ciphertext, 0, output, 12, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, output, 12 + ciphertext.Length, 16);
        return output;
    }

    private static bool TryDecrypt(byte[] sealedBytes, byte[] key, out byte[] plaintext)
    {
        plaintext = Array.Empty<byte>();
        if (sealedBytes.Length < 28) return false;
        var nonce = sealedBytes.AsSpan(0, 12);
        var tag = sealedBytes.AsSpan(sealedBytes.Length - 16, 16);
        var ciphertext = sealedBytes.AsSpan(12, sealedBytes.Length - 28);
        plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            CrashLog.Write("ProfileDelete", ex);
        }
    }
}
