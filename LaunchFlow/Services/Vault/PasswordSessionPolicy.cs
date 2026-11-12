namespace LaunchFlow.Services;

/// <summary>
/// Password-manager tab session timing. Separate from Windows/vault lock grace
/// (<see cref="SessionLockPolicy"/>): this only gates the Passwords UI after
/// an explicit master-password unlock.
/// </summary>
public static class PasswordSessionPolicy
{
    public static readonly TimeSpan Duration = TimeSpan.FromHours(1);

    public static bool IsActive(bool passwordsUnlocked, bool vaultUnlocked, DateTimeOffset expiresUtc, DateTimeOffset now) =>
        passwordsUnlocked && vaultUnlocked && now < expiresUtc;

    public static DateTimeOffset RenewFrom(DateTimeOffset now) => now.Add(Duration);
}
