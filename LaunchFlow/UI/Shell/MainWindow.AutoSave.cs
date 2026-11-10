using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LaunchFlow.Services;

namespace LaunchFlow;

/// <summary>
/// WPF adapter for the independent AutoSaveSchedule. Changes are debounced
/// on the UI thread and committed using the existing encrypted durable-write
/// boundary; a failed write is never reported as saved or retried in a loop.
/// Credential text is explicitly excluded from the dirty-field subscriptions.
/// </summary>
public partial class MainWindow
{
    private readonly AutoSaveSchedule _autoSaveSchedule = new();
    private readonly DispatcherTimer _autoSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(200)
    };
    private bool _autoSaving;
    private (bool Start, bool TaskScheduler)? _lastAppliedStartupRegistration;

    private void StartAutoSave()
    {
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;
    }

    private void AutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        PersistPendingAutoSave(immediate: false);
    }

    private void WebsiteEnabledChanged()
    {
        if (_loadingEditor || _configuration is null || _exiting) return;
        MarkUnsavedChanges();
        // Enable/disable is a one-click decision. Commit it before moving to
        // a different website without requiring the separate Save button.
        PersistPendingAutoSave(immediate: true);
    }

    private void PersistPendingAutoSave(bool immediate)
    {
        if (!_autoSaveSchedule.HasPendingChanges)
        {
            _autoSaveTimer.Stop();
            return;
        }
        bool canSave =
            !_autoSaving && !_saveInProgress && !_vaultLockInProgress && !_windowsLocked && !_exiting &&
            !_loadingEditor && !_installerShutdownInProgress && !HasPendingEditorDialog &&
            !_runUi.IsRunning && VaultDatabase.Current.IsUnlocked;
        if (!canSave || (!immediate && !_autoSaveSchedule.Ready(DateTimeOffset.UtcNow, canSave))) return;

        // Immediate toggles and regular debounced edits use the same encrypted
        // transaction, ownership checks and failed-write policy.
        _autoSaveSchedule.Attempted();
        _autoSaving = true;
        SaveStatusLabel.Text = "Saving…";
        SaveStatusLabel.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(174, 197, 221));
        try
        {
            if (!SaveAll())
                ShowAutoSaveFailed();
        }
        finally
        {
            _autoSaving = false;
            // A later edit re-arms the timer; a failed write never loops.
            if (_autoSaveSchedule.CanAttempt) _autoSaveTimer.Start();
            else _autoSaveTimer.Stop();
        }
    }

    private void ShowAutoSaveFailed()
    {
        _editorSession.MarkFailed();
        SaveStatusLabel.Text = "Save failed";
        SaveStatusLabel.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 155, 134));
        SaveChangesButton.FontWeight = FontWeights.Bold;
        SaveChangesButton.Content = "Retry save";
        SaveChangesButton.ToolTip =
            "Changes are NOT saved. Fix invalid fields or storage access, then click Retry save.";
    }
}
