using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Win32;
using Microsoft.Playwright;
using Forms = System.Windows.Forms;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace LaunchFlow;

/// <summary>Windows session lock monitoring and post-unlock workflow scheduling.</summary>
public partial class MainWindow
{
    private void HandleSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
            Dispatcher.BeginInvoke(new Action(OnWindowsLocked));
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
            Dispatcher.BeginInvoke(new Action(() => _ = OnWindowsUnlockedAsync()));
    }

    private void OnWindowsLocked()
    {
        if (_windowsLocked) return; // Duplicate Windows events must not create timers.
        _windowsLocked = true;
        UpdateRunButtons();
        _lockedSinceUtc = DateTimeOffset.UtcNow;
        _unlockCancellation?.Cancel();
        _runCancellation?.Cancel(); // Don't start new steps while the device is locked.
        _lockTimeoutCancellation?.Cancel();
        _lockTimeoutCancellation?.Dispose();
        _lockTimeoutCancellation = new CancellationTokenSource();
        _ = LockAfterThresholdAsync(_lockedSinceUtc.Value, _lockTimeoutCancellation.Token);
        AppendLog($"Windows locked. Vault locks after {_configuration.VaultLockMinutes} minutes unless Windows unlocks first.");
    }

    private async Task LockAfterThresholdAsync(DateTimeOffset lockTime, CancellationToken token)
    {
        try
        {
            await Task.Delay(LockTimeout, token);
            await Dispatcher.InvokeAsync(new Func<Task>(async () =>
            {
                // Check the actual elapsed lock interval; timers may be late
                // after laptop sleep/resume and events may arrive out of order.
                if (_windowsLocked && _lockedSinceUtc == lockTime &&
                    DateTimeOffset.UtcNow - lockTime >= LockTimeout)
                    await SecureVaultLockAsync("Windows has been locked for the configured timeout: encrypted vault locked.");
            })).Task.Unwrap();
        }
        catch (OperationCanceledException) { }
    }


    private async Task OnWindowsUnlockedAsync()
    {
        if (!_windowsLocked) return; // Ignore duplicate unlock events.
        var interval = _lockedSinceUtc.HasValue ? DateTimeOffset.UtcNow - _lockedSinceUtc.Value : TimeSpan.MaxValue;
        _windowsLocked = false;
        _lockedSinceUtc = null;
        _lockTimeoutCancellation?.Cancel();
        _lockTimeoutCancellation?.Dispose();
        _lockTimeoutCancellation = null;
        if (interval >= LockTimeout)
            await SecureVaultLockAsync($"Windows was locked for {interval.TotalMinutes:F1} minutes; master password required.");
        else await _vaultLockTask;
        UpdateRunButtons();
        if (_windowsLocked || _exiting) return;
        if (!VaultDatabase.Current.IsUnlocked)
        {
            ShowFromTray();
            if (!RequireMasterPassword(forcePrompt: true))
            {
                AppendLog("Vault remains locked. No credential automation will run.");
                return;
            }
            AppendLog("Encrypted vault unlocked after Windows unlock.");
        }
        else AppendLog($"Windows unlocked after {interval.TotalSeconds:F0} seconds; continuing existing vault session.");
        if (_configuration.RunOnUnlock) await RunAfterUnlockAsync();
    }

    private async Task RunAfterUnlockAsync()
    {
        _unlockCancellation?.Cancel();
        _unlockCancellation?.Dispose();
        _unlockCancellation = new CancellationTokenSource();
        try
        {
            int seconds = Math.Clamp(_configuration.UnlockDelaySeconds, 0, 300);
            AppendLog($"Windows session unlocked: starting in {seconds} seconds.");
            await Task.Delay(TimeSpan.FromSeconds(seconds), _unlockCancellation.Token);
            await RunEnabledAsync();
        }
        catch (OperationCanceledException) { /* superseded by another session event */ }
    }

}
