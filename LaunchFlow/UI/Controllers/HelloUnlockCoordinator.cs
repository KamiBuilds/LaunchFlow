using LaunchFlow.Models;

namespace LaunchFlow.Controllers;

/// <summary>
/// Owns Windows Hello enable/disable operation epochs and durable preference
/// writes. WPF prompts, MessageBox, vault reads and WindowsHelloService calls
/// stay in MainWindow (keeps workspace test stubs free of vault/Hello types).
/// </summary>
public sealed class HelloUnlockCoordinator
{
    private long _enableEpoch;

    public long BeginEnable()
    {
        checked { return ++_enableEpoch; }
    }

    public void InvalidateOutstandingEnables()
    {
        checked { _enableEpoch++; }
    }

    public bool StillOwnsEnable(
        long enableEpoch,
        Configuration ownedConfig,
        Configuration currentConfig,
        long ownedVaultSession,
        long currentVaultSession,
        bool vaultUnlocked,
        bool windowsLocked,
        bool vaultLockInProgress,
        bool exiting) =>
        enableEpoch == _enableEpoch &&
        ReferenceEquals(currentConfig, ownedConfig) &&
        vaultUnlocked &&
        currentVaultSession == ownedVaultSession &&
        !windowsLocked && !vaultLockInProgress && !exiting;

    public bool TryPersistEnabled(
        Configuration ownedConfig,
        long ownedVaultSession,
        Action<Configuration, long> saveDurable,
        Action disableHello)
    {
        ArgumentNullException.ThrowIfNull(ownedConfig);
        ArgumentNullException.ThrowIfNull(saveDurable);
        ArgumentNullException.ThrowIfNull(disableHello);
        ownedConfig.WindowsHelloUnlockEnabled = true;
        try
        {
            saveDurable(ownedConfig, ownedVaultSession);
            return true;
        }
        catch
        {
            ownedConfig.WindowsHelloUnlockEnabled = false;
            disableHello();
            throw;
        }
    }

    /// <summary>
    /// Invalidates outstanding enables, runs <paramref name="disableHello"/>,
    /// clears the preference flag, and persists only while ownership holds.
    /// Returns false when ownership refuses persistence; save failures also
    /// return false after local disable (preference may remain enabled on disk).
    /// </summary>
    public bool TryDisableAndPersist(
        Configuration ownedConfig,
        Configuration currentConfig,
        long ownedVaultSession,
        long currentVaultSession,
        bool vaultUnlocked,
        bool windowsLocked,
        bool vaultLockInProgress,
        bool exiting,
        Action disableHello,
        Action<Configuration, long> saveDurable)
    {
        ArgumentNullException.ThrowIfNull(ownedConfig);
        ArgumentNullException.ThrowIfNull(currentConfig);
        ArgumentNullException.ThrowIfNull(disableHello);
        ArgumentNullException.ThrowIfNull(saveDurable);
        InvalidateOutstandingEnables();
        disableHello();
        ownedConfig.WindowsHelloUnlockEnabled = false;
        if (!vaultUnlocked ||
            currentVaultSession != ownedVaultSession ||
            !ReferenceEquals(currentConfig, ownedConfig) ||
            windowsLocked || vaultLockInProgress || exiting)
            return false;
        try
        {
            saveDurable(ownedConfig, ownedVaultSession);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
