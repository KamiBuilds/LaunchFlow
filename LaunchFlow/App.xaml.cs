using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using LaunchFlow.Services;
using LaunchFlow.Models;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace LaunchFlow;

public partial class App : Application
{
    // One editor/tray process per interactive Windows session.  A second launch
    // contacts the original via a per-session, current-user-only named pipe.
    private const string MutexName = @"Local\LaunchFlow.Windows.SingleInstance";
    private static readonly string ActivationPipeName =
        $"LaunchFlow.Activate.Session{Process.GetCurrentProcess().SessionId}";

    private System.Threading.Mutex? _singleInstance;
    private CancellationTokenSource? _listenerCancellation;
    private bool _restorePending;
    private string? _pendingOpenPath;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Self-contained Windows Setup ships LaunchFlow, the .NET runtime and app
        // libraries. Browser automation uses installed Edge/Chrome channels;
        // never silently download or install a separate browser executable.
        Environment.SetEnvironmentVariable("PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD", "1");

        DispatcherUnhandledException += (_, args) =>
        {
            // Fatal callbacks have no browser-close barrier. Revoke the key before
            // diagnostics/UI, but never wrap or delete a potentially live profile here.
            try { VaultDatabase.Current.Lock(); } catch { /* best effort on a failing process */ }
            try { CrashLog.Write("UI", args.Exception); } catch { /* diagnostics must not prevent revocation */ }
            try
            {
                if (MainWindow is LaunchFlow.MainWindow window) window.RequestCrashCleanup();
            }
            catch { /* best effort: coordinated cleanup may be unavailable */ }
            MessageBox.Show(
                "LaunchFlow hit an unexpected problem. Vault access was revoked. Browser cleanup is best effort; profiles are retained unless coordinated cleanup completes. Retry Lock vault before continuing.",
                "LaunchFlow", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            // A terminating thread cannot safely await the engine or use the dispatcher.
            // Leave profile sources untouched for recovery; cleanup is not guaranteed.
            try { VaultDatabase.Current.Lock(); } catch { /* best effort on a failing process */ }
            if (args.ExceptionObject is Exception ex)
                try { CrashLog.Write("Fatal", ex); } catch { /* diagnostics are best effort */ }
            try
            {
                MessageBox.Show(
                    "LaunchFlow needs to close after an unexpected error. Vault access was revoked. Browser profiles were not sealed by this fatal callback and may need cleanup after restart.",
                    "LaunchFlow", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* UI may be unavailable */ }
        };

        _pendingOpenPath = FindOpenablePath(e.Args);
        var installerShutdown = e.Args.Any(arg =>
            string.Equals(arg, "--exit-for-install", StringComparison.OrdinalIgnoreCase));

        _singleInstance = new System.Threading.Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            if (installerShutdown)
            {
                // Setup observes the original process mutex; no second vault unlock or UI.
                RequestExistingWindow(null, forInstaller: true);
            }
            else if (!RequestExistingWindow(_pendingOpenPath))
                MessageBox.Show(
                    "LaunchFlow is already running, but its window could not be restored. " +
                    "Open it from the LaunchFlow icon beside the Windows clock, or exit it from that menu and try again.",
                    "LaunchFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        if (installerShutdown)
        {
            // The installed application may have exited between Setup's check and launch.
            // Never start a vault session just to handle a shutdown request.
            Shutdown();
            return;
        }

        base.OnStartup(e);
        // A locked database cannot load workflow configuration. Unlock or migrate
        // before constructing MainWindow; no startup automation may run beforehand.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _listenerCancellation = new CancellationTokenSource();
        _ = Task.Run(() => ListenForActivationAsync(_listenerCancellation.Token));
        var startInTray = e.Args.Any(a =>
            string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        // Opening a .launchflow file should show the UI, not stay hidden in the tray.
        if (_pendingOpenPath is not null) startInTray = false;
        try
        {
            // Verify external managed/native dependencies before showing the
            // master-password wizard or opening/creating the encrypted vault.
            // This probe uses only a transient in-memory SQLite connection.
            var compatibility = WindowsApplicationControl.CheckRequiredComponents();
            if (!compatibility.Ready)
            {
                MessageBox.Show(compatibility.Message, "LaunchFlow cannot start on this PC",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }
            if (!InitializeVault()) { Shutdown(); return; }
            var window = new MainWindow();
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            if (startInTray)
            {
                // Avoid a visible flash when Windows autostart launches LaunchFlow.
                window.WindowState = WindowState.Minimized;
                window.ShowInTaskbar = false;
                window.Show();
                window.Hide();
                window.ShowInTaskbar = true;
            }
            else
            {
                window.Show();
            }

            if (_pendingOpenPath is not null)
            {
                var path = _pendingOpenPath;
                _pendingOpenPath = null;
                window.QueueOpenFile(path);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("LaunchFlow could not safely open its vault. Existing files are unchanged.\n\n" + ex.Message,
                "LaunchFlow vault", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
    }

    internal static string? FindOpenablePath(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;
            if (arg.StartsWith('-')) continue;
            try
            {
                var full = Path.GetFullPath(arg.Trim('"'));
                if (!File.Exists(full)) continue;
                var ext = Path.GetExtension(full);
                if (ext.Equals(".launchflow", StringComparison.OrdinalIgnoreCase))
                    return full;
            }
            catch { /* ignore bad paths */ }
        }
        return null;
    }

    private static bool InitializeVault()
    {
        // After a "forgot password" reset, fall through to first-run setup.
        for (int round = 0; round < 2; round++)
        {
            var db = VaultDatabase.Current;
            if (db.Exists)
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    var dialog = new UnlockWindow();
                    if (dialog.ShowDialog() != true)
                    {
                        if (dialog.ResetRequested)
                            break; // vault deleted; retry outer loop for setup
                        return false;
                    }
                    if (string.IsNullOrEmpty(dialog.Password)) continue;
                    if (db.Unlock(dialog.Password))
                    {
                        try { ProfileVaultService.UnsealAll(); }
                        catch (Exception ex)
                        {
                            CrashLog.Write("ProfileUnseal", ex);
                            MessageBox.Show("Some browser sessions could not be restored. Their encrypted files were retained. " +
                                "Affected workflows are blocked rather than replacing those sessions. Your saved vault logins are separate.",
                                "Browser sessions need attention", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                        return true;
                    }
                    MessageBox.Show("Incorrect master password.", "Vault locked",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                if (!VaultDatabase.Current.Exists) continue;
                return false;
            }

            if (LegacyMigration.HasLegacy)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var dialog = new SecretPromptWindow("Upgrade your LaunchFlow vault",
                        "Enter your previous LaunchFlow password to move your saved websites into the updated secure storage. Your existing files will be kept.");
                    if (dialog.ShowDialog() != true) return false;
                    if (!LegacyMigration.VerifyOldMaster(dialog.Secret))
                    {
                        MessageBox.Show("Incorrect previous master password. Your old data has not been changed.",
                            "Cannot migrate", MessageBoxButton.OK, MessageBoxImage.Warning);
                        continue;
                    }
                    var oldData = LegacyMigration.ReadLegacyData();
                    db.Initialize(dialog.Secret, oldData.Config, oldData.Secrets);
                    return true;
                }
                return false;
            }

            var setup = new SetupWindow();
            if (setup.ShowDialog() != true) return false;
            var settings = new Configuration
            {
                StartWithWindows = setup.StartWithWindows,
                RunOnUnlock = setup.RunAfterUnlock,
                RunOnAppStart = setup.RunOnAppStart,
                MinimizeToTray = setup.RunInTray,
                VaultLockMinutes = 10,
                SealBrowserProfilesOnLock = true
            };
            db.Initialize(setup.ChosenPassword, settings);
            try { StartupService.Apply(settings.StartWithWindows); }
            catch (Exception ex)
            {
                Debug.WriteLine("Could not register Windows startup: " + ex.Message);
            }
            return true;
        }
        return false;
    }

    private static bool RequestExistingWindow(string? openPath, bool forInstaller = false)
    {
        // Retry briefly: Windows may start the second process while the first is
        // still creating its main window and named-pipe listener.
        var payload = forInstaller ? "EXIT_FOR_INSTALL" :
            openPath is null ? "SHOW" : "OPEN|" + openPath;
        for (int attempt = 0; attempt < 18; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".", ActivationPipeName, PipeDirection.Out, PipeOptions.None);
                client.Connect(250);
                using var writer = new StreamWriter(client, Encoding.UTF8, 1024, leaveOpen: true)
                {
                    AutoFlush = true
                };
                writer.WriteLine(payload);
                return true;
            }
            catch (TimeoutException) { }
            catch (IOException) { }
            Thread.Sleep(120);
        }
        return false;
    }

    private async Task ListenForActivationAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    ActivationPipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8, true, 1024, leaveOpen: true);
                var message = await reader.ReadLineAsync(cancellation).ConfigureAwait(false);
                if (string.IsNullOrEmpty(message)) continue;

                if (message.StartsWith("OPEN|", StringComparison.OrdinalIgnoreCase))
                {
                    var path = message["OPEN|".Length..].Trim();
                    // Queue on the UI dispatcher; the named-pipe listener must remain responsive.
                    _ = Dispatcher.BeginInvoke(new Action(() => HandleExternalOpen(path)));
                }
                else if (string.Equals(message, "SHOW", StringComparison.Ordinal))
                {
                    // Deliberately fire-and-forget the UI notification (CS4014).
                    _ = Dispatcher.BeginInvoke(new Action(RestoreRunningWindow));
                }
                else if (string.Equals(message, "EXIT_FOR_INSTALL", StringComparison.Ordinal) ||
                         string.Equals(message, "EXIT_FOR_BUILD", StringComparison.Ordinal))
                {
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (MainWindow is LaunchFlow.MainWindow window && window.IsLoaded)
                            _ = window.RequestShutdownForInstallAsync();
                    }));
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (!cancellation.IsCancellationRequested)
            {
                // A second launch may disconnect while we are reading; keep listening.
            }
            catch (Exception ex) when (!cancellation.IsCancellationRequested)
            {
                Debug.WriteLine($"LaunchFlow activation listener: {ex}");
                try { await Task.Delay(300, cancellation).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void HandleExternalOpen(string path)
    {
        if (MainWindow is LaunchFlow.MainWindow window && window.IsLoaded)
            window.QueueOpenFile(path);
        else
        {
            _pendingOpenPath = path;
            _restorePending = true;
        }
    }

    private void RestoreRunningWindow()
    {
        if (MainWindow is LaunchFlow.MainWindow window && window.IsLoaded)
            window.RestoreFromExternalLaunch();
        else
            _restorePending = true;
    }

    internal void NotifyMainWindowReady(LaunchFlow.MainWindow window)
    {
        if (_pendingOpenPath is not null)
        {
            var path = _pendingOpenPath;
            _pendingOpenPath = null;
            _restorePending = false;
            window.QueueOpenFile(path);
            return;
        }

        if (!_restorePending) return;
        _restorePending = false;
        window.RestoreFromExternalLaunch();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _listenerCancellation?.Cancel();
        VaultDatabase.Current.Lock();
        // The listener observes cancellation and disposes its pipe. No blocking
        // wait here: exit should never freeze if a client process is misbehaving.
        try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
