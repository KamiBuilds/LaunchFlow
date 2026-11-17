namespace LaunchFlow.Services;

/// <summary>
/// Pure decision logic for the Windows lock/unlock threshold. Callers retain
/// responsibility for cancellation, credential revocation and browser cleanup.
/// </summary>
public static class SessionLockPolicy
{
    public static TimeSpan Timeout(int configuredMinutes) =>
        TimeSpan.FromMinutes(Math.Clamp(configuredMinutes, 1, 120));

    public static bool RequiresVaultLock(TimeSpan elapsed, TimeSpan threshold)
    {
        if (threshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        // Monotonic clock corrections must not accidentally extend access.
        return elapsed < TimeSpan.Zero || elapsed >= threshold;
    }
}
