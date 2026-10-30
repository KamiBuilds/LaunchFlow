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
                // A browser can close at the same moment as installer shutdown,
                // leaving a transient Playwright close error. Retry the remaining
                // tracked contexts once, never kill a browser or discard profiles.
                var recovered = false;
                if (_installerShutdownInProgress)
                {
                    await Task.Delay(350);
                    try
                    {
                        await _runner.CloseSessionsAsync();
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
                    if (_installerShutdownInProgress) _installerProfileSealFailed = true;
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

}
