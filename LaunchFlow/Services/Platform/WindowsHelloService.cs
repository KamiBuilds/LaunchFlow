using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Windows.Security.Credentials.UI;

namespace LaunchFlow.Services;

/// <summary>
/// Optional Windows Hello gate in front of a DPAPI-protected unlock secret.
/// Periodically requires the master password again (re-auth interval).
/// </summary>
public static class WindowsHelloService
{
    private static readonly object Gate = new();
    private static long _publicationGeneration;
    private static long _disableEpoch;

    private static string SecretPath =>
        Path.Combine(VaultDatabase.Current.DataDirectory, "hello-unlock.bin");

    private static string MetaPath =>
        Path.Combine(VaultDatabase.Current.DataDirectory, "hello-meta.json");

    public static bool IsEnabled => File.Exists(SecretPath);

    /// <summary>Monotonic owner for hello-unlock.bin publication/cleanup.</summary>
    public static long PublicationGeneration
    {
        get { lock (Gate) return _publicationGeneration; }
    }

    public enum CleanupResult
    {
        Removed,
        Superseded,
        RemovalFailed
    }

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

    /// <returns>Publication generation that owns the written file, or null on failure/supersession.</returns>
    public static async Task<long?> EnableAsync(string masterPassword)
    {
        if (string.IsNullOrEmpty(masterPassword)) return null;
        if (!await VerifyUserAsync("Enable Windows Hello unlock for LaunchFlow").ConfigureAwait(true))
            return null;

        long disableEpoch;
        lock (Gate) disableEpoch = _disableEpoch;

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(masterPassword),
            optionalEntropy: Encoding.UTF8.GetBytes("LaunchFlow.Hello.v1"),
            scope: DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(VaultDatabase.Current.DataDirectory);
        var staging = Path.Combine(
            VaultDatabase.Current.DataDirectory,
            "hello-unlock." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(staging, protectedBytes).ConfigureAwait(true);
            CryptographicOperations.ZeroMemory(protectedBytes);
            protectedBytes = Array.Empty<byte>();

            long ownedPublication;
            lock (Gate)
            {
                // Reset/disable that finished while we staged must win (F-097-05).
                if (_disableEpoch != disableEpoch)
                    return null;
                ownedPublication = ++_publicationGeneration;
                File.Move(staging, SecretPath, overwrite: true);
                staging = string.Empty;
            }
            MarkPasswordVerified();
            return ownedPublication;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (protectedBytes.Length > 0)
                CryptographicOperations.ZeroMemory(protectedBytes);
            if (!string.IsNullOrEmpty(staging))
            {
                try { File.Delete(staging); } catch { /* ignore */ }
            }
        }
    }

    public static void Disable()
    {
        try { ForceRemoveUnlocked(); }
        catch { /* best-effort UI path; reset uses DisableStrict */ }
    }

    /// <summary>
    /// Removes Hello unlock material and fails if the secret file still exists.
    /// Used by vault reset so a claimed wipe cannot leave a recoverable master password.
    /// </summary>
    public static void DisableStrict()
    {
        try { ForceRemoveUnlocked(); }
        catch (Exception ex)
        {
            throw new IOException("Windows Hello unlock material could not be removed.", ex);
        }
        if (File.Exists(SecretPath))
            throw new IOException("Windows Hello unlock material is still present after disable.");
    }

    /// <summary>
    /// Stale-enable cleanup: delete only while <paramref name="publication"/> still owns the file.
    /// Propagates deletion outcomes so the UI can warn (F-097-04).
    /// </summary>
    public static CleanupResult DisableIfPublication(long publication)
    {
        lock (Gate)
        {
            if (_publicationGeneration != publication)
                return CleanupResult.Superseded;
            try
            {
                if (File.Exists(SecretPath)) File.Delete(SecretPath);
                if (File.Exists(MetaPath)) File.Delete(MetaPath);
            }
            catch
            {
                return CleanupResult.RemovalFailed;
            }
            if (_publicationGeneration != publication)
                return CleanupResult.Superseded;
            if (File.Exists(SecretPath))
                return CleanupResult.RemovalFailed;
            return CleanupResult.Removed;
        }
    }

    private static void ForceRemoveUnlocked()
    {
        lock (Gate)
        {
            checked { _disableEpoch++; }
            checked { _publicationGeneration++; }
            if (File.Exists(SecretPath)) File.Delete(SecretPath);
            if (File.Exists(MetaPath)) File.Delete(MetaPath);
        }
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
