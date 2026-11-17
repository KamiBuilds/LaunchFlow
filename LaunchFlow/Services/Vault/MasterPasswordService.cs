namespace LaunchFlow.Services;

public sealed class MasterPasswordService
{
    public bool IsConfigured => VaultDatabase.Current.Exists;

    public bool Verify(string password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        // Already unlocked: confirm without ZeroKey / SessionGeneration bump /
        // cache reload (Enable Hello and other in-session prompts rely on this).
        if (VaultDatabase.Current.IsUnlocked)
            return VaultDatabase.Current.ConfirmPassword(password);

        return VaultDatabase.Current.Unlock(password);
    }

    public void Change(string currentPassword, string newPassword)
    {
        VaultDatabase.Current.ChangeMasterPassword(currentPassword, newPassword);
        // Leave vault locked after a password change; caller re-prompts.
        VaultDatabase.Current.Lock();
    }
}
