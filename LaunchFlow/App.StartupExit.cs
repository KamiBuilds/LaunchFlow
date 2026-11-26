using System.Windows;

namespace LaunchFlow;

public partial class App
{
    private bool _startupExitRequested;
    private bool _startupInitializationInProgress;
    private bool _startupShutdownIssued;

    private static bool StartupExitRequested => (Current as App)?._startupExitRequested == true;

    private void RequestInstallerExit()
    {
        if (!_startupInitializationInProgress && MainWindow is LaunchFlow.MainWindow window &&
            Windows.OfType<Window>().Contains(window))
        {
            // An unloaded shell may already own an engine; retain its save/drain/refusal gate.
            _ = window.RequestShutdownForInstallAsync();
            return;
        }

        if (_startupExitRequested) return;
        _startupExitRequested = true;
        _pendingOpenPath = null;
        _restorePending = false;
        foreach (var dialog in Windows.OfType<Window>().Where(w => w is not LaunchFlow.MainWindow).ToArray())
            dialog.Close();
        // Modal frames must unwind before shutdown revokes a potentially in-flight vault session.
        if (!_startupInitializationInProgress) ShutdownStartupOnce();
    }

    private void ShutdownStartupOnce()
    {
        if (MainWindow is LaunchFlow.MainWindow window && Windows.OfType<Window>().Contains(window))
        {
            // Constructed windows own cleanup; an already-closed reset shell no longer does.
            _ = window.RequestShutdownForInstallAsync();
            return;
        }
        if (_startupShutdownIssued) return;
        _startupShutdownIssued = true;
        Shutdown();
    }

    private bool InitializeVaultForStartup()
    {
        if (_startupExitRequested || _startupInitializationInProgress) return false;
        _startupInitializationInProgress = true;
        try { return InitializeVault() && !_startupExitRequested; }
        finally { _startupInitializationInProgress = false; }
    }

    private LaunchFlow.MainWindow? CreateStartupMainWindow()
    {
        if (_startupExitRequested) return null;
        _startupInitializationInProgress = true;
        LaunchFlow.MainWindow window;
        try { window = new MainWindow(); }
        finally { _startupInitializationInProgress = false; }
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        if (!_startupExitRequested) return window;
        // Constructor reentrancy can cancel startup after an engine was allocated.
        // Never bypass that constructed shell's durable-save and cleanup refusal gate.
        _ = window.RequestShutdownForInstallAsync();
        return null;
    }

    private static bool ShowStartupDialog(Window dialog)
    {
        if (StartupExitRequested) return false;
        return dialog.ShowDialog() == true && !StartupExitRequested;
    }
}
