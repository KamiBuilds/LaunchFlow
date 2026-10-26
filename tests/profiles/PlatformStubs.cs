using System.Security.Cryptography;

namespace LaunchFlow.Services
{
    public sealed class VaultDatabase
    {
        public static VaultDatabase Current { get; } = new();
        public string DataDirectory { get; set; } = "";
    }
    public static class CrashLog
    {
        public static List<string> Failures { get; } = new();
        public static void Write(string label, Exception error) => Failures.Add(label + ": " + error.GetType().Name);
    }
}

namespace System.Security.Cryptography
{
    public enum DataProtectionScope { CurrentUser }

    // Contract simulation ONLY: randomized authenticated wrapping, not native DPAPI assurance.
    public static class ProtectedData
    {
        private static readonly byte[] TestWrappingKey = SHA256.HashData("fixture-only-wrapping-key"u8);
        public static byte[] Protect(byte[] input, byte[]? entropy, DataProtectionScope scope)
        {
            var result = new byte[12 + input.Length + 16];
            RandomNumberGenerator.Fill(result.AsSpan(0, 12));
            using var aes = new AesGcm(TestWrappingKey, 16);
            aes.Encrypt(result.AsSpan(0, 12), input, result.AsSpan(12, input.Length), result.AsSpan(12 + input.Length));
            return result;
        }
        public static byte[] Unprotect(byte[] input, byte[]? entropy, DataProtectionScope scope)
        {
            var result = new byte[input.Length - 28];
            using var aes = new AesGcm(TestWrappingKey, 16);
            aes.Decrypt(input.AsSpan(0, 12), input.AsSpan(12, result.Length), input.AsSpan(12 + result.Length), result);
            return result;
        }
    }
}
