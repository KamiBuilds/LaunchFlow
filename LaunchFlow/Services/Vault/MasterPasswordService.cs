namespace LaunchFlow.Services;

/// <summary>
/// Thin helper over <see cref="VaultDatabase"/> master-password operations.
/// </summary>
public sealed class MasterPasswordService
{
    public bool IsConfigured => VaultDatabase.Current.Exists;

    public bool Verify(string password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        if (VaultDatabase.Current.IsUnlocked)
        {
            // Already unlocked this session: re-verify by attempting a fresh unlock
            // against a cloned derivation without wiping the current key on failure.
            // Simplest correct path: Unlock replaces the key only on success.
            return VaultDatabase.Current.Unlock(password);
        }

        return VaultDatabase.Current.Unlock(password);
    }

    public void Change(string currentPassword, string newPassword)
    {
        VaultDatabase.Current.ChangeMasterPassword(currentPassword, newPassword);
        // Leave vault locked after a password change; caller re-prompts.
        VaultDatabase.Current.Lock();
    }
}
