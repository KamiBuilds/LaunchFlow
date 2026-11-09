using System.IO;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LaunchFlow.Services;

/// <summary>
/// Authenticated v3 envelope codec. The expected owner and purpose are supplied
/// by the caller's authoritative operation, never by untrusted sealed bytes.
/// </summary>
internal static class VaultRecordCodec
{
    public const int Version = 3;
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("LFV3");
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("LaunchFlow.Record");
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private static byte[] Context(byte[] vaultId, string purpose, string recordId)
    {
        if (vaultId.Length != 16) throw new InvalidDataException("Invalid vault identifier.");
        byte domainCode = purpose switch
        {
            "verifier" => 1,
            "config" => 2,
            "credential" => 3,
            _ => throw new InvalidDataException("Unknown encrypted record purpose.")
        };
        if (purpose == "credential")
        {
            if (!Guid.TryParseExact(recordId, "N", out var guid) ||
                guid == Guid.Empty || guid.ToString("N") != recordId)
                throw new InvalidDataException("Malformed credential workflow identity.");
        }
        else if (recordId != purpose)
        {
            throw new InvalidDataException("Wrong record identity.");
        }

        var owner = Encoding.UTF8.GetBytes(recordId);
        var bytes = new byte[Domain.Length + 1 + 4 + 1 + 16 + 4 + owner.Length + Header.Length];
        var at = 0;
        Domain.CopyTo(bytes, at); at += Domain.Length;
        bytes[at++] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at, 4), Version); at += 4;
        bytes[at++] = domainCode;
        vaultId.CopyTo(bytes, at); at += vaultId.Length;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at, 4), owner.Length); at += 4;
        owner.CopyTo(bytes, at); at += owner.Length;
        Header.CopyTo(bytes, at);
        return bytes;
    }

    public static byte[] Seal(byte[] plaintext, byte[] key, byte[] vaultId, string purpose, string recordId)
    {
        var aad = Context(vaultId, purpose, recordId);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var output = new byte[Header.Length + NonceLength + plaintext.Length + TagLength];
        Header.CopyTo(output, 0);
        nonce.CopyTo(output, Header.Length);
        using var gcm = new AesGcm(key, TagLength);
        gcm.Encrypt(nonce, plaintext,
            output.AsSpan(Header.Length + NonceLength, plaintext.Length),
            output.AsSpan(output.Length - TagLength), aad);
        return output;
    }

    public static byte[] Open(byte[] envelope, byte[] key, byte[] vaultId, string purpose, string recordId)
    {
        var aad = Context(vaultId, purpose, recordId);
        if (envelope.Length < Header.Length + NonceLength + TagLength ||
            !envelope.AsSpan(0, Header.Length).SequenceEqual(Header))
            throw new CryptographicException("Encrypted record format is not v3.");

        var ciphertextLength = envelope.Length - Header.Length - NonceLength - TagLength;
        var plaintext = new byte[ciphertextLength];
        try
        {
            using var gcm = new AesGcm(key, TagLength);
            gcm.Decrypt(
                envelope.AsSpan(Header.Length, NonceLength),
                envelope.AsSpan(Header.Length + NonceLength, ciphertextLength),
                envelope.AsSpan(envelope.Length - TagLength, TagLength),
                plaintext, aad);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }
}
