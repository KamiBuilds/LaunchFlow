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
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(package, JsonOptions));
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[payload.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(key, TagSize))
                aes.Encrypt(nonce, payload, ciphertext, tag);

            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            writer.Write(Encoding.ASCII.GetBytes(Magic));
            writer.Write(salt);
            writer.Write(nonce);
            writer.Write(tag);
            writer.Write(ciphertext.Length);
            writer.Write(ciphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static PortablePackage Read(string path, string exportPassword)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        var magic = Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length));
        if (magic != Magic)
            throw new InvalidDataException("Not a LaunchFlow encrypted backup.");

        var salt = reader.ReadBytes(SaltSize);
        var nonce = reader.ReadBytes(NonceSize);
        var tag = reader.ReadBytes(TagSize);
        var length = reader.ReadInt32();
        if (length < 0 || length > 64 * 1024 * 1024)
            throw new InvalidDataException("Backup payload is invalid.");
        var ciphertext = reader.ReadBytes(length);

        var key = DeriveKey(exportPassword, salt);
        try
        {
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            PortablePackage? parsed = JsonSerializer.Deserialize<PortablePackage>(
                Encoding.UTF8.GetString(plaintext), JsonOptions);
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
            CryptographicOperations.ZeroMemory(key);
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
