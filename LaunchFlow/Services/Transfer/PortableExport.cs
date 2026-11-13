using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using LaunchFlow.Models;

namespace LaunchFlow.Services;

/// <summary>
/// AES-256-GCM portable .launchflow backup files (export password ≠ vault master).
/// </summary>
public static class PortableExport
{
    private const string Magic = "LFLW1";
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int ArgonMemoryKb = 64 * 1024;
    private const int ArgonIterations = 3;
    private const int ArgonLanes = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public sealed class PortablePackage
    {
        public Configuration Config { get; set; } = new();
        public Dictionary<Guid, SiteCredential> Credentials { get; set; } = new();
    }

    public static void Write(string path, string exportPassword, PortablePackage package)
    {
        if (string.IsNullOrWhiteSpace(exportPassword) || exportPassword.Length < PasswordStrength.MinimumLength)
            throw new ArgumentException(
                $"Export password must be at least {PasswordStrength.MinimumLength} characters.");

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = DeriveKey(exportPassword, salt);
        try
        {
            // Serialize directly into a clearable UTF-8 buffer: no immutable JSON string carrying credentials.
            var payload = JsonSerializer.SerializeToUtf8Bytes(package, JsonOptions);
            try
            {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[payload.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(key, TagSize))
                aes.Encrypt(nonce, payload, ciphertext, tag);

            var destination = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(destination)
                ?? throw new InvalidOperationException("Backup destination has no parent directory.");
            var temporary = Path.Combine(directory,
                "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
                    writer.Write(Encoding.ASCII.GetBytes(Magic));
                    writer.Write(salt);
                    writer.Write(nonce);
                    writer.Write(tag);
                    writer.Write(ciphertext.Length);
                    writer.Write(ciphertext);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                var verified = Read(temporary, exportPassword);
                if (verified.Credentials.Count != package.Credentials.Count ||
                    verified.Config.Workflows.Count != package.Config.Workflows.Count)
                    throw new InvalidDataException("The staged encrypted backup did not pass verification.");

                if (File.Exists(destination))
                    File.Replace(temporary, destination, null);
                else
                    File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static PortablePackage Read(string path, string exportPassword)
    {
        using var stream = File.OpenRead(path);
        // Reject corrupt/truncated headers before spending 64 MiB on Argon2id.
        if (stream.Length < Magic.Length + SaltSize + NonceSize + TagSize + sizeof(int))
            throw new InvalidDataException("Backup header is incomplete.");
        using var reader = new BinaryReader(stream);
        var magic = Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length));
        if (magic != Magic)
            throw new InvalidDataException("Not a LaunchFlow encrypted backup.");

        var salt = reader.ReadBytes(SaltSize);
        var nonce = reader.ReadBytes(NonceSize);
        var tag = reader.ReadBytes(TagSize);
        var length = reader.ReadInt32();
        if (length < 0 || length > 64 * 1024 * 1024 ||
            stream.Length - stream.Position != length)
            throw new InvalidDataException("Backup payload is invalid or truncated.");
        var ciphertext = reader.ReadBytes(length);
        byte[]? plaintext = null;
        byte[]? key = null;
        try
        {
            key = DeriveKey(exportPassword, salt);
            plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            // Deserialize UTF-8 directly; do not create an additional
            // immutable string containing every password in the backup.
            var parsed = JsonSerializer.Deserialize<PortablePackage>(
                plaintext.AsSpan(), JsonOptions);
            if (parsed is null)
                throw new InvalidDataException("Backup JSON is empty.");
            return parsed;
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Incorrect export password or corrupted backup.");
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = ArgonLanes,
            MemorySize = ArgonMemoryKb,
            Iterations = ArgonIterations
        };
        return argon.GetBytes(KeySize);
    }
}
