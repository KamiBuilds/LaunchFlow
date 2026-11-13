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

/// <summary>Coordinated browser teardown and secure vault locking.</summary>
public partial class MainWindow
{
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

    private Task? _profileSealTask;

    private async Task SecureVaultLockCoreAsync(string message)
    {
        _vaultLockInProgress = true;
        _vaultCleanupFailed = false;
        _profileSealFailed = false;
        PublishKeePassCache(null);
        bool cleanupComplete = false;
        var pendingRun = _runUi.ActiveTask;
        var pendingTeach = _teachUi.ActiveTask;
        try
        {
            // Revoke first. CloseSessions is the engine's cancellation/dispatch barrier;
            // awaiting a dispatched fill before closing its context can stall forever.
            VaultDatabase.Current.Lock();
            UpdateRunButtons();
            try { _runUi.Cancellation?.Cancel(); }
            catch (Exception ex) { AppendLog("Cancellation callback failed during lock: " + ex.GetType().Name); }
            // Stop the modeless recorder before closing browser contexts.
            // Only then can the full Teach task safely drain its callbacks.
            if (!CloseActiveRecorderForLock()) return;
            LockPasswordManager(message);
            ClearCredentialEditor();
            try { await ExitCleanupWait.RequireAsync(_runner.CloseSessionsAsync(),
                    TimeSpan.FromSeconds(20), "Browser shutdown"); }
            catch (Exception ex)
            {
                // A browser can close at the same moment as installer shutdown,
                // leaving a transient Playwright close error. Retry the remaining
                // tracked contexts once, never kill a browser or discard profiles.
                var recovered = false;
                if (_installerShutdownInProgress)
                {
                    await Task.Delay(350);
                    try
                    {
                        await ExitCleanupWait.RequireAsync(_runner.CloseSessionsAsync(),
                            TimeSpan.FromSeconds(20), "Browser shutdown retry");
                        recovered = true;
                    }
                    catch (Exception retryError)
                    {
                        if (_configuration.DiagnosticsLogEnabled)
                            CrashLog.Write("CloseSessionsRetry", retryError);
                    }
                }
                if (!recovered)
                {
                    AppendLog("Browser cleanup did not complete; profiles retained and automation blocked. Close browser windows and retry Lock vault.");
                    NotifyTray("Vault locked", "Browser cleanup did not complete; profiles retained and automation blocked. Close browser windows and retry Lock vault.", Forms.ToolTipIcon.Warning);
                    if (_configuration.DiagnosticsLogEnabled) CrashLog.Write("CloseSessions", ex);
                    return; // Never wrap/delete a profile while its browser may still be live.
                }
            }
            try
            {
                if (pendingRun is not null)
                    await ExitCleanupWait.RequireAsync(pendingRun, TimeSpan.FromSeconds(12), "Automation cancellation");
            }
            catch (InvalidOperationException ex) when (ex.InnerException is TimeoutException)
            {
                AppendLog("Automation cancellation is still running; profile sealing and exit remain blocked.");
                return;
            }
            catch (Exception ex) { AppendLog("Automation ended during lock: " + ex.GetType().Name); }
            try
            {
                if (pendingTeach is not null)
                    await pendingTeach.WaitAsync(TimeSpan.FromSeconds(12));
            }
            catch (OperationCanceledException)
            {
                // Browser closure canceled page acquisition as intended.
                AppendLog("Teach acquisition was canceled during lock.");
            }
            catch (Exception ex)
            {
                // Timeout or failed callback drain is not a successful cleanup.
                // Preserve profiles and leave the existing retry/blocked gate.
                AppendLog("Teach operation did not drain; browser profiles retained and lock retry required: " +
                    ex.GetType().Name);
                return;
            }
            if (_configuration.SealBrowserProfilesOnLock)
            {
                if (_exiting) WorkflowStatusLabel.Text = "Closing LaunchFlow — encrypting browser profiles…";
                _profileSealTask ??= Task.Run(ProfileVaultService.SealAll);
                try
                {
                    await ExitCleanupWait.RequireAsync(_profileSealTask, TimeSpan.FromSeconds(60),
                        "Browser-profile encryption and cleanup");
                    _profileSealTask = null;
                }
                catch (Exception ex)
                {
                    // A timed-out seal continues under the same task ownership.
                    // Never start a second seal while the first is still using the directory.
                    if (_profileSealTask?.IsCompleted == true) _profileSealTask = null;
                    _profileSealFailed = true;
                    if (_installerShutdownInProgress) _installerProfileSealFailed = true;
                    AppendLog("Could not seal browser profiles; source files retained: " + ex.GetType().Name);
                    if (_configuration.DiagnosticsLogEnabled) CrashLog.Write("SealProfiles", ex);
                    return;
                }
            }
            cleanupComplete = true;
        }
        finally
        {
            _vaultLockInProgress = !cleanupComplete;
            _vaultCleanupFailed = !cleanupComplete;
            UpdateRunButtons();
        }
    }

}
