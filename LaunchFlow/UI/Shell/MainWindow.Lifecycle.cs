using System.Windows;
using LaunchFlow.Services;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace LaunchFlow;

/// <summary>
/// Thin WPF adapter for the shared user/tray/installer close completion barrier.
/// </summary>
public partial class MainWindow
{
    private readonly ExitCompletionGate _exitDrain = new();

    private async Task CompleteOwnedExitAsync(bool installer)
    {
        if (_exitDrain.Permitted) return;
        _exiting = true;
        IsEnabled = false;
        UpdateRunButtons();
        try
        {
            await _exitDrain.PrepareAsync(async () =>
            {
                await SecureVaultLockAsync(installer
                    ? "Installer requested safe shutdown."
                    : "User requested safe exit.");

                if (installer && _installerProfileSealFailed)
                    throw new InvalidOperationException(
                        "Encrypted-profile sealing failed. The original files were retained; retry Lock vault.");
                if (_profileSealFailed)
                    throw new InvalidOperationException(
                        "Browser profiles could not be sealed. The original files were retained.");
                if (_vaultCleanupFailed || _vaultLockInProgress)
                    throw new InvalidOperationException(
                        "Browser session cleanup did not finish safely. Profiles are retained; retry Lock vault.");
                if (_editorSession.IsDirty || HasPendingEditorDialog)
                    throw new InvalidOperationException(
                        "New unsaved changes appeared. Unlock and save them before exiting.");

                await _runner.DisposeAsync();
            });
            Close();
        }
        catch (Exception ex)
        {
            _exitDrain.RevokePermit();
            _exiting = false;
            IsEnabled = true;
            UpdateRunButtons();
            AppendLog("Safe exit paused: " + ex.GetType().Name);
            if (installer)
                NotifyTray("LaunchFlow update paused",
                    "Saving or browser cleanup did not complete safely. Fix the issue and retry.",
                    Forms.ToolTipIcon.Warning);
            else
            {
                ShowFromTray();
                MessageBox.Show(this, ex.Message, "LaunchFlow exit paused",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private async Task DisposeAfterUnexpectedCloseAsync()
    {
        try { await _vaultLockTask; } catch { }
        try { await _runner.DisposeAsync(); } catch { }
        _runUi.Cancellation?.Dispose();
        _unlockCancellation?.Dispose();
    }
}
