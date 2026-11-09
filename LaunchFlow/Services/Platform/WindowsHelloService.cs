using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Security.Credentials.UI;

namespace LaunchFlow.Services;

/// <summary>
/// Optional Windows Hello gate in front of a DPAPI-protected unlock secret.
/// Periodically requires the master password again (re-auth interval).
/// </summary>
public static class WindowsHelloService
{
    private static string SecretPath =>
        Path.Combine(VaultDatabase.Current.DataDirectory, "hello-unlock.bin");

    private static string MetaPath =>
        Path.Combine(VaultDatabase.Current.DataDirectory, "hello-meta.json");

    public static bool IsEnabled => File.Exists(SecretPath);

    public static async Task<bool> IsSupportedAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return false;
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            return availability == UserConsentVerifierAvailability.Available;
        }
        catch
        {
            return false;
        }
    }

    public static bool NeedsPasswordReauth(int reauthDays)
    {
        if (!IsEnabled) return false;
        if (reauthDays <= 0) return false;
        try
        {
            if (!File.Exists(MetaPath)) return true;
            var meta = JsonSerializer.Deserialize<HelloMeta>(File.ReadAllText(MetaPath));
            if (meta?.LastPasswordVerifiedUtc is null) return true;
            if (!DateTimeOffset.TryParse(meta.LastPasswordVerifiedUtc, out var last)) return true;
            return DateTimeOffset.UtcNow - last > TimeSpan.FromDays(reauthDays);
        }
        catch
        {
            return true;
        }
    }

    public static void MarkPasswordVerified()
    {
        try
        {
            Directory.CreateDirectory(VaultDatabase.Current.DataDirectory);
            var meta = new HelloMeta { LastPasswordVerifiedUtc = DateTimeOffset.UtcNow.ToString("o") };
            File.WriteAllText(MetaPath, JsonSerializer.Serialize(meta));
        }
        catch { /* ignore */ }
    }

    public static async Task<bool> EnableAsync(string masterPassword)
    {
        if (string.IsNullOrEmpty(masterPassword)) return false;
        if (!await VerifyUserAsync("Enable Windows Hello unlock for LaunchFlow").ConfigureAwait(true))
            return false;

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(masterPassword),
            optionalEntropy: Encoding.UTF8.GetBytes("LaunchFlow.Hello.v1"),
            scope: DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(VaultDatabase.Current.DataDirectory);
        await File.WriteAllBytesAsync(SecretPath, protectedBytes).ConfigureAwait(true);
        MarkPasswordVerified();
        return true;
    }

    public static void Disable()
    {
        try
        {
            if (File.Exists(SecretPath)) File.Delete(SecretPath);
            if (File.Exists(MetaPath)) File.Delete(MetaPath);
        }
        catch { /* ignore */ }
    }

    public static async Task<string?> TryUnlockPasswordAsync(int reauthDays = 30)
    {
        if (!IsEnabled) return null;
        if (NeedsPasswordReauth(reauthDays))
            return null; // caller should ask for master password

        if (!await VerifyUserAsync("Unlock LaunchFlow").ConfigureAwait(true))
            return null;

        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(SecretPath).ConfigureAwait(true);
            var plain = ProtectedData.Unprotect(
                protectedBytes,
                optionalEntropy: Encoding.UTF8.GetBytes("LaunchFlow.Hello.v1"),
                scope: DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            Disable();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> VerifyUserAsync(string message)
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            if (availability != UserConsentVerifierAvailability.Available)
                return false;
            var result = await UserConsentVerifier.RequestVerificationAsync(message);
            return result == UserConsentVerificationResult.Verified;
        }
        catch
        {
            return false;
        }
    }

    private sealed class HelloMeta
    {
        public string? LastPasswordVerifiedUtc { get; set; }
    }
}
