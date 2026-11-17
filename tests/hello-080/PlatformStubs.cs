using System.Collections.Generic;

namespace LaunchFlow.Services
{
    // Deliberately inert synthetic root, never a real Windows vault.
    public sealed class VaultDatabase
    {
        public static VaultDatabase Current { get; } = new();
        public string DataDirectory { get; set; } = "";
    }
}

namespace System.Security.Cryptography
{
    public enum DataProtectionScope { CurrentUser }
    public static class ProtectedData
    {
        public static byte[] Protect(byte[] plaintext, byte[]? optionalEntropy, DataProtectionScope scope) =>
            plaintext.ToArray();
        public static byte[] Unprotect(byte[] encrypted, byte[]? optionalEntropy, DataProtectionScope scope) =>
            encrypted.ToArray();
    }
}

namespace Windows.Security.Credentials.UI
{
    public enum UserConsentVerifierAvailability { Available, Unavailable }
    public enum UserConsentVerificationResult { Verified, Canceled }
    public static class UserConsentVerifier
    {
        private static readonly object Gate = new();
        private static readonly List<TaskCompletionSource<UserConsentVerificationResult>> Calls = new();
        public static bool AutoApprove { get; set; }

        public static Task<UserConsentVerifierAvailability> CheckAvailabilityAsync() =>
            Task.FromResult(UserConsentVerifierAvailability.Available);

        public static Task<UserConsentVerificationResult> RequestVerificationAsync(string message)
        {
            if (AutoApprove) return Task.FromResult(UserConsentVerificationResult.Verified);
            var wait = new TaskCompletionSource<UserConsentVerificationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Gate) Calls.Add(wait);
            return wait.Task;
        }

        public static void ResetPending()
        {
            lock (Gate) Calls.Clear();
            AutoApprove = false;
        }

        public static int PendingCount
        {
            get { lock (Gate) return Calls.Count; }
        }

        public static void Approve(int index)
        {
            TaskCompletionSource<UserConsentVerificationResult> next;
            lock (Gate) next = Calls[index];
            next.TrySetResult(UserConsentVerificationResult.Verified);
        }
    }
}
