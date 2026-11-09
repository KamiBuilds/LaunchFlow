using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Buffers.Binary;

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
            if (IsReservedProfileName(Path.GetFileName(dir))) continue;
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
            throw new AggregateException("Some browser profiles could not finish sealing or cleanup. Encrypted snapshots and remaining plaintext were retained.", failures);
    }

    public static void UnsealAll()
    {
        if (!Directory.Exists(SealedRoot)) return;
        Directory.CreateDirectory(ProfilesRoot);

        var failures = new List<Exception>();
        foreach (var file in Directory.GetFiles(SealedRoot, "*.profile.aes"))
        {
            if (IsReservedProfileName(Path.GetFileName(file))) continue;
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
            // Keep the origin/both envelope: rollback readers cannot open LFPS2.
            ZipFile.CreateFromDirectory(directory, zip, CompressionLevel.Optimal, includeBaseDirectory: false);
            plain = zip.ToArray();
            var sealedBytes = Encrypt(plain, key);
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(sealedBytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temp, sealedPath, overwrite: true);
            // Recursive deletion can partially succeed: remove this generation from
            // browser/seal admission atomically before deleting any of its contents.
            var disposal = Path.Combine(ProfilesRoot, ".sealed-" + Guid.NewGuid().ToString("N"));
            Directory.Move(directory, disposal);
            Directory.Delete(disposal, recursive: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
            if (zip.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan());
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static bool IsReservedProfileName(string name) =>
        name.StartsWith(".restore-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(".sealed-", StringComparison.OrdinalIgnoreCase);

    private static void UnsealFile(string sealedPath)
    {
        var id = Path.GetFileName(sealedPath).Replace(".profile.aes", "", StringComparison.OrdinalIgnoreCase);
        var target = Path.Combine(ProfilesRoot, id);
        if (Directory.Exists(target)) return;

        var key = ProfileKey();
        var staging = Path.Combine(ProfilesRoot, ".restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            using var source = File.OpenRead(sealedPath);
            Span<byte> signature = stackalloc byte[5];
            if (source.Length < signature.Length)
                throw new CryptographicException("Invalid browser-profile archive.");
            source.ReadExactly(signature);

            if (signature.SequenceEqual("LFPS2"u8))
            {
                // Seeking must not spill a decrypted ZIP to disk. Preallocate once so
                // MemoryStream growth cannot leave abandoned plaintext arrays behind.
                using var zip = new MemoryStream((int)Math.Min(source.Length, MaxChunkedZipBytes));
                try
                {
                    DecryptChunks(source, zip, key);
                    zip.Position = 0;
                    ExtractArchive(zip, staging);
                }
                finally
                {
                    if (zip.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan());
                }
            }
            else
            {
                source.Position = 0;
                var bytes = File.ReadAllBytes(sealedPath);
                try
                {
                    if (!TryDecrypt(bytes, key, out var plaintext))
                        throw new CryptographicException("Browser-profile archive cannot be decrypted.");
                    try
                    {
                        using var archiveStream = new MemoryStream(plaintext, writable: false);
                        ExtractArchive(archiveStream, staging);
                    }
                    finally { CryptographicOperations.ZeroMemory(plaintext); }
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            Directory.Move(staging, target);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static void ExtractArchive(Stream zip, string staging)
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        long total = 0;
        if (archive.Entries.Count > 200_000)
            throw new InvalidDataException("Browser profile has too many entries.");
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > 4L * 1024 * 1024 * 1024 ||
                total > 8L * 1024 * 1024 * 1024 - entry.Length)
                throw new InvalidDataException("Browser profile exceeds its extraction limit.");
            total += entry.Length;
        }
        archive.ExtractToDirectory(staging, overwriteFiles: false);
    }

    // Budget is the decrypted ZIP payload, not the expanded profile (bounded separately).
    private const int MaxChunkedZipBytes = 256 * 1024 * 1024;

    private static void DecryptChunks(Stream source, Stream destination, byte[] key)
    {
        using var aes = new AesGcm(key, 16);
        long index = 0;
        Span<byte> size = stackalloc byte[4];
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> tag = stackalloc byte[16];
        Span<byte> aad = stackalloc byte[12];
        while (true)
        {
            source.ReadExactly(size);
            var length = BinaryPrimitives.ReadInt32LittleEndian(size);
            if (length is < 0 or > 64 * 1024)
                throw new CryptographicException("Invalid browser-profile chunk length.");
            source.ReadExactly(nonce);
            var ciphertext = new byte[length];
            var plaintext = new byte[length];
            try
            {
                source.ReadExactly(ciphertext);
                source.ReadExactly(tag);
                BinaryPrimitives.WriteInt64LittleEndian(aad, index);
                BinaryPrimitives.WriteInt32LittleEndian(aad[8..], length);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
                if (length == 0)
                {
                    if (source.ReadByte() != -1)
                        throw new CryptographicException("Browser-profile archive has trailing data.");
                    break;
                }
                if (destination.Length > MaxChunkedZipBytes - length)
                    throw new InvalidDataException("Browser-profile ZIP exceeds its 256 MiB memory limit. Encrypted files were retained.");
                destination.Write(plaintext);
                index++;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ciphertext);
                CryptographicOperations.ZeroMemory(plaintext);
            }
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
