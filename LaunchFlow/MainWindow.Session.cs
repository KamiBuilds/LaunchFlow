using System.Windows;
using LaunchFlow.Services;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using Forms = System.Windows.Forms;

namespace LaunchFlow;

public partial class MainWindow
{
    private TimeSpan LockTimeout => TimeSpan.FromMinutes(Math.Clamp(_configuration.VaultLockMinutes, 1, 120));

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
        _windowsSessionGeneration++;
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

    private Task SecureVaultLockAsync(string message)
    {
        if (!_vaultLockTask.IsCompleted) return _vaultLockTask;
        // Publish ownership before cancellation callbacks or modal reentrancy can lock again.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _vaultLockTask = completion.Task;
        async Task CleanupAsync()
        {
            try { await SecureVaultLockCoreAsync(message); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
        _ = CleanupAsync();
        return completion.Task;
    }

    private async Task SecureVaultLockCoreAsync(string message)
    {
        _vaultLockInProgress = true;
        _vaultCleanupFailed = false;
        _keepassMasterPassword = null;
        bool cleanupComplete = false;
        var pendingRun = _activeRunTask;
        var pendingTeach = _activeTeachTask;
        try
        {
            // Revoke first. CloseSessions is the engine's cancellation/dispatch barrier;
            // awaiting a dispatched fill before closing its context can stall forever.
            VaultDatabase.Current.Lock();
            UpdateRunButtons();
            try { _runCancellation?.Cancel(); }
            catch (Exception ex) { AppendLog("Cancellation callback failed during lock: " + ex.GetType().Name); }
            LockPasswordManager(message);
            ClearCredentialEditor();
            try { await _runner.CloseSessionsAsync(); }
            catch (Exception ex)
            {
                AppendLog("Browser cleanup did not complete; profiles retained and automation blocked. Retry Lock vault.");
                NotifyTray("Vault locked", "Browser cleanup did not complete; profiles retained and automation blocked. Retry Lock vault.", Forms.ToolTipIcon.Warning);
                if (_configuration.DiagnosticsLogEnabled) CrashLog.Write("CloseSessions", ex);
                return; // Never wrap/delete a profile while its browser may still be live.
            }
            try { if (pendingRun is not null) await pendingRun; }
            catch (Exception ex) { AppendLog("Automation ended during lock: " + ex.GetType().Name); }
            try { if (pendingTeach is not null) await pendingTeach; }
            catch (Exception ex) { AppendLog("Teach ended during lock: " + ex.GetType().Name); }
            cleanupComplete = true;
            if (_configuration.SealBrowserProfilesOnLock)
            {
                try { await Task.Run(ProfileVaultService.SealAll); }
                catch (Exception ex)
                {
                    AppendLog("Could not seal browser profiles; source files retained: " + ex.GetType().Name);
                    if (_configuration.DiagnosticsLogEnabled) CrashLog.Write("SealProfiles", ex);
                }
            }
        }
        finally
        {
            _vaultLockInProgress = !cleanupComplete;
            _vaultCleanupFailed = !cleanupComplete;
            UpdateRunButtons();
        }
    }

    private async Task OnWindowsUnlockedAsync()
    {
        if (!_windowsLocked) return; // Ignore duplicate unlock events.
        var generation = _windowsSessionGeneration;
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
        // A newer lock/unlock can finish while this handler awaits old cleanup.
        if (_windowsLocked || _exiting || generation != _windowsSessionGeneration) return;
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
        var cancellation = new CancellationTokenSource();
        _unlockCancellation = cancellation;
        var token = cancellation.Token;
        var generation = _windowsSessionGeneration;
        try
        {
            int seconds = Math.Clamp(_configuration.UnlockDelaySeconds, 0, 300);
            AppendLog($"Windows session unlocked: starting in {seconds} seconds.");
            await Task.Delay(TimeSpan.FromSeconds(seconds), token);
            token.ThrowIfCancellationRequested();
            if (generation != _windowsSessionGeneration) return;
            await RunEnabledAsync();
        }
        catch (OperationCanceledException) { /* superseded by another session event */ }
        finally
        {
            if (ReferenceEquals(_unlockCancellation, cancellation)) _unlockCancellation = null;
            cancellation.Dispose();
        }
    }

    private bool RequireMasterPassword(bool forcePrompt = false)
    {
        if (_vaultLockInProgress || _windowsLocked || _exiting) return false;
        if (!forcePrompt && PasswordSessionIsValid()) return true;
        if (!_master.IsConfigured) return false;
        var generation = _windowsSessionGeneration;
        var prompt = new UnlockWindow { Owner = this };
        if (prompt.ShowDialog() != true || string.IsNullOrEmpty(prompt.Password)) return false;
        if (_vaultLockInProgress || _windowsLocked || _exiting || generation != _windowsSessionGeneration) return false;
        try
        {
            if (_master.Verify(prompt.Password))
            {
                try { ProfileVaultService.UnsealAll(); }
                catch (Exception ex)
                {
                    AppendLog("Could not unseal browser profiles: " + ex.Message);
                    MessageBox.Show(this, "Some browser sessions could not be restored. Their encrypted files were retained. " +
                        "Affected workflows are blocked; your saved vault logins are separate.",
                        "Browser sessions need attention", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                _passwordsUnlocked = true;
                _passwordSessionExpiresUtc = DateTimeOffset.UtcNow.Add(PasswordSessionDuration);
                PasswordSessionTimer_Tick(null, EventArgs.Empty);
                return true;
            }
        }
        catch (Exception ex) { AppendLog("Master password verification error: " + ex.GetType().Name); }
        MessageBox.Show(this, "Incorrect master password.", "Not unlocked", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }
}
