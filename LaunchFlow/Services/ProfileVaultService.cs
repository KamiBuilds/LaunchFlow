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
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var encryptor = new ChunkedArchiveWriter(output, key))
                {
                    ZipFile.CreateFromDirectory(directory, encryptor, CompressionLevel.Optimal, includeBaseDirectory: false);
                    encryptor.Finish();
                }
                output.Flush(flushToDisk: true);
            }
            File.Move(temp, sealedPath, overwrite: true);
            Directory.Delete(directory, recursive: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

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
                // Read-mode ZipArchive requires seeking; the isolated temporary ZIP is
                // deleted by Windows as soon as its exclusive file handle closes.
                var temp = Path.Combine(ProfilesRoot, ".zip-" + Guid.NewGuid().ToString("N"));
                using var zip = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
                DecryptChunks(source, zip, key);
                zip.Position = 0;
                ExtractArchive(zip, staging);
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

    private static void DecryptChunks(Stream source, Stream destination, byte[] key)
    {
        using var aes = new AesGcm(key, 16);
        long index = 0;
        while (true)
        {
            Span<byte> size = stackalloc byte[4];
            source.ReadExactly(size);
            var length = BinaryPrimitives.ReadInt32LittleEndian(size);
            if (length is < 0 or > 64 * 1024)
                throw new CryptographicException("Invalid browser-profile chunk length.");
            Span<byte> nonce = stackalloc byte[12];
            source.ReadExactly(nonce);
            var ciphertext = new byte[length];
            var plaintext = new byte[length];
            Span<byte> tag = stackalloc byte[16];
            try
            {
                source.ReadExactly(ciphertext);
                source.ReadExactly(tag);
                Span<byte> aad = stackalloc byte[12];
                BinaryPrimitives.WriteInt64LittleEndian(aad, index);
                BinaryPrimitives.WriteInt32LittleEndian(aad[8..], length);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
                if (length == 0)
                {
                    if (source.ReadByte() != -1)
                        throw new CryptographicException("Browser-profile archive has trailing data.");
                    break;
                }
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

    private sealed class ChunkedArchiveWriter : Stream
    {
        private readonly Stream _output;
        private readonly AesGcm _aes;
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _count;
        private long _index;
        private bool _finished;

        public ChunkedArchiveWriter(Stream output, byte[] key)
        {
            _output = output;
            _aes = new AesGcm(key, 16);
            _output.Write("LFPS2"u8);
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> bytes)
        {
            if (_finished) throw new ObjectDisposedException(nameof(ChunkedArchiveWriter));
            while (!bytes.IsEmpty)
            {
                var length = Math.Min(bytes.Length, _buffer.Length - _count);
                bytes[..length].CopyTo(_buffer.AsSpan(_count));
                _count += length;
                bytes = bytes[length..];
                if (_count == _buffer.Length) WriteChunk(_count);
            }
        }

        private void WriteChunk(int length)
        {
            Span<byte> nonce = stackalloc byte[12];
            RandomNumberGenerator.Fill(nonce);
            Span<byte> tag = stackalloc byte[16];
            Span<byte> aad = stackalloc byte[12];
            BinaryPrimitives.WriteInt64LittleEndian(aad, _index);
            BinaryPrimitives.WriteInt32LittleEndian(aad[8..], length);
            var encrypted = new byte[length];
            try
            {
                _aes.Encrypt(nonce, _buffer.AsSpan(0, length), encrypted, tag, aad);
                Span<byte> size = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(size, length);
                _output.Write(size);
                _output.Write(nonce);
                _output.Write(encrypted);
                _output.Write(tag);
                _index++;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
                CryptographicOperations.ZeroMemory(_buffer.AsSpan(0, length));
                _count = 0;
            }
        }

        public void Finish()
        {
            if (_finished) return;
            if (_count > 0) WriteChunk(_count);
            WriteChunk(0);
            _finished = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CryptographicOperations.ZeroMemory(_buffer);
                _aes.Dispose();
            }
            base.Dispose(disposing);
        }

        public override void Flush() => _output.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_finished;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
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
