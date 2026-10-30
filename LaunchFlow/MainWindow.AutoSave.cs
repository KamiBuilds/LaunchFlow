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
        if (!_autoSaveSchedule.HasPendingChanges)
        {
            _autoSaveTimer.Stop();
            return;
        }
        bool canSave =
            !_autoSaving && !_vaultLockInProgress && !_windowsLocked && !_exiting &&
            !_loadingEditor && !_installerShutdownInProgress && !HasPendingEditorDialog &&
            !_runInProgress && VaultDatabase.Current.IsUnlocked;
        if (!_autoSaveSchedule.Ready(DateTimeOffset.UtcNow, canSave)) return;

        // Mark before SaveAll, because its synchronous WPF setters may fire
        // nested events; avoid duplicate saves and keep the UI responsive.
        _autoSaveSchedule.Attempted();
        _autoSaving = true;
        SaveStatusLabel.Text = "Saving…";
        SaveStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(174, 197, 221));
        try
        {
            if (!SaveAll())
                ShowAutoSaveFailed();
        }
        finally
        {
            _autoSaving = false;
            // A failed write is retried on a new edit or an explicit Save.
            // Keep pending changes in memory for the existing close/update gate.
            _autoSaveTimer.Stop();
        }
    }

    private void ShowAutoSaveFailed()
    {
        _hasUnsavedChanges = true;
        SaveStatusLabel.Text = "Save failed";
        SaveStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(255, 155, 134));
        SaveChangesButton.FontWeight = FontWeights.Bold;
        SaveChangesButton.Content = "Retry save";
        SaveChangesButton.ToolTip =
            "Changes are NOT saved. Fix invalid fields or storage access, then click Retry save.";
    }
}
