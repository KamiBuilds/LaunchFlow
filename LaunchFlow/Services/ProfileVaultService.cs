using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace LaunchFlow.Services;

/// <summary>
/// Encrypts Playwright browser profile folders at rest when the vault locks,
/// and restores them after unlock. Profiles are plaintext only while unlocked
/// (browsers need direct filesystem access).
/// </summary>
public static class ProfileVaultService
{
    public static string ProfilesRoot =>
        Path.Combine(VaultBackupService.DataDirectory, "profiles-v2");

    public static string SealedRoot =>
        Path.Combine(VaultBackupService.DataDirectory, "profiles-sealed");

    public static void SealAll()
    {
        if (!Directory.Exists(ProfilesRoot)) return;
        Directory.CreateDirectory(SealedRoot);

        foreach (var dir in Directory.GetDirectories(ProfilesRoot))
        {
            try
            {
                SealDirectory(dir);
            }
            catch (Exception ex)
            {
                CrashLog.Write("ProfileSeal", ex);
            }
        }
    }

    public static void UnsealAll()
    {
        if (!Directory.Exists(SealedRoot)) return;
        Directory.CreateDirectory(ProfilesRoot);

        foreach (var file in Directory.GetFiles(SealedRoot, "*.profile.aes"))
        {
            try
            {
                UnsealFile(file);
            }
            catch (Exception ex)
            {
                CrashLog.Write("ProfileUnseal", ex);
            }
        }
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
        var tempZip = Path.Combine(Path.GetTempPath(), $"lf-profile-{id}-{Guid.NewGuid():N}.zip");
        try
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
            ZipFile.CreateFromDirectory(directory, tempZip, CompressionLevel.Optimal, includeBaseDirectory: false);
            var plain = File.ReadAllBytes(tempZip);
            var key = ProfileKey();
            var sealedBytes = Encrypt(plain, key);
            File.WriteAllBytes(sealedPath, sealedBytes);
            CryptographicOperations.ZeroMemory(key);
            TryDeleteDir(directory);
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { /* ignore */ }
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
            var tempZip = Path.Combine(Path.GetTempPath(), $"lf-profile-out-{id}-{Guid.NewGuid():N}.zip");
            try
            {
                File.WriteAllBytes(tempZip, zipBytes);
                Directory.CreateDirectory(target);
                ZipFile.ExtractToDirectory(tempZip, target, overwriteFiles: true);
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { /* ignore */ }
                CryptographicOperations.ZeroMemory(zipBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Key is machine+user DPAPI material mixed with a fixed app salt.
    /// Profiles stay bound to this Windows account (same as DPAPI).
    /// </summary>
    private static byte[] ProfileKey()
    {
        var seed = Encoding.UTF8.GetBytes(
            Environment.UserName + "|" + Environment.MachineName + "|LaunchFlow.Profiles.v1");
        var protectedSeed = ProtectedData.Protect(seed, null, DataProtectionScope.CurrentUser);
        return SHA256.HashData(protectedSeed);
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
