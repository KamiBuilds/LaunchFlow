namespace LaunchFlow.Services;

/// <summary>
/// Explicit user exit must not turn an unlocked save failure or a locked dirty
/// editor into silent data loss. No WPF dependencies: shared with regression tests.
/// </summary>
public static class ExitSafety
{
    public static bool TryPersistForExit(Func<bool> vaultUnlocked,
        Func<bool> hasUnsavedChanges, Func<bool> save)
    {
        ArgumentNullException.ThrowIfNull(vaultUnlocked);
        ArgumentNullException.ThrowIfNull(hasUnsavedChanges);
        ArgumentNullException.ThrowIfNull(save);

        // Already locked and clean is a legitimate exit; no password prompt.
        if (!vaultUnlocked()) return !hasUnsavedChanges();
        if (!save()) return false;
        // A save callback can reenter WPF and change the pending editor.
        return !hasUnsavedChanges();
    }
}
