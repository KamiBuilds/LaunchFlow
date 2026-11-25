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
        StartupDiagnostics.Record(StartupDiagnostics.Stage.ProcessStarted);
        // Self-contained Windows Setup ships LaunchFlow, the .NET runtime, app
        // libraries, Playwright driver, and (when packaged) Playwright Firefox
        // under playwright-browsers/. Edge/Chrome still use installed channels.
        // Never silently download browsers at runtime without consent.
        PlaywrightFirefoxSupport.ConfigureProcessBrowsersPath();
        Environment.SetEnvironmentVariable("PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD", "1");

        DispatcherUnhandledException += (_, args) =>
        {
            // Fatal callbacks have no browser-close barrier. Revoke the key before
            // diagnostics/UI, but never wrap or delete a potentially live profile here.
            try { VaultDatabase.Current.Lock(); } catch { /* best effort on a failing process */ }
            try { CrashLog.Write("UI", args.Exception); } catch { /* diagnostics must not prevent revocation */ }
            StartupDiagnostics.Record(StartupDiagnostics.Stage.UiUnhandledException, args.Exception);
            try
            {
                if (MainWindow is LaunchFlow.MainWindow window) window.RequestCrashCleanup();
            }
            catch { /* best effort: coordinated cleanup may be unavailable */ }
            args.Handled = true;
            var error = StartupDiagnostics.RootCause(args.Exception) ?? args.Exception;
            try
            {
                MessageBox.Show(
                    "LaunchFlow encountered a UI error and locked the credential vault. " +
                    "Browser sessions are retained until coordinated cleanup completes.\n\n" +
                    $"Diagnostic: {error.GetType().Name} (0x{unchecked((uint)error.HResult):X8})\n" +
                    "Log: %LOCALAPPDATA%\\LaunchFlow\\startup-diagnostics.log\n\n" +
                    "Restart LaunchFlow. If this error returns, send the last log entries so " +
                    "the failing component can be fixed without deleting your passwords.",
                    "LaunchFlow startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* An error dialog failure must not trigger another UI crash. */ }
            // A failed startup must not leave an invisible process holding the
            // single-instance mutex and preventing all subsequent launches.
            if (MainWindow is not LaunchFlow.MainWindow { IsLoaded: true }) Shutdown();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            // A terminating thread cannot safely await the engine or use the dispatcher.
            // Leave profile sources untouched for recovery; cleanup is not guaranteed.
            try { VaultDatabase.Current.Lock(); } catch { /* best effort on a failing process */ }
            if (args.ExceptionObject is Exception ex)
            {
                try { CrashLog.Write("Fatal", ex); } catch { /* diagnostics are best effort */ }
                StartupDiagnostics.Record(StartupDiagnostics.Stage.FatalException, ex);
            }
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
            StartupDiagnostics.Record(StartupDiagnostics.Stage.ExistingInstance);
            if (installerShutdown)
            {
                // Setup observes the original process mutex; no second vault unlock or UI.
                RequestExistingWindow(null, forInstaller: true);
            }
            else if (!RequestExistingWindow(_pendingOpenPath))
            {
                StartupDiagnostics.Record(StartupDiagnostics.Stage.ActivationFailed);
                MessageBox.Show(
                    "LaunchFlow is already running, but its window could not be restored. " +
                    "Open it from the LaunchFlow icon beside the Windows clock, or exit it from that menu and try again.",
                    "LaunchFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            }
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
            StartupDiagnostics.Record(StartupDiagnostics.Stage.DependencyProbeStarted);
            var compatibility = WindowsApplicationControl.CheckRequiredComponents();
            if (!compatibility.Ready)
            {
                StartupDiagnostics.Record(StartupDiagnostics.Stage.DependencyProbeRejected);
                MessageBox.Show(compatibility.Message, "LaunchFlow cannot start on this PC",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }
            StartupDiagnostics.Record(StartupDiagnostics.Stage.VaultUnlockStarted);
            if (!InitializeVault())
            {
                StartupDiagnostics.Record(StartupDiagnostics.Stage.VaultUnlockCancelled);
                Shutdown();
                return;
            }
            StartupDiagnostics.Record(StartupDiagnostics.Stage.VaultReady);
            StartupDiagnostics.Record(StartupDiagnostics.Stage.WindowConstructionStarted);
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

            StartupDiagnostics.Record(StartupDiagnostics.Stage.WindowShown);
            if (_pendingOpenPath is not null)
            {
                var path = _pendingOpenPath;
                _pendingOpenPath = null;
                window.QueueOpenFile(path);
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Record(StartupDiagnostics.Stage.StartupException, ex);
            // Avoid displaying exception.Message: paths or credential material
            // from third-party exceptions must not be copied into dialogs.
            MessageBox.Show("LaunchFlow failed during startup. Your encrypted vault has not been reset.\n\n" +
                $"Error: {ex.GetType().Name} (0x{unchecked((uint)ex.HResult):X8})\n" +
                "Diagnostic log: %LOCALAPPDATA%\\LaunchFlow\\startup-diagnostics.log",
                "LaunchFlow startup error", MessageBoxButton.OK, MessageBoxImage.Error);
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

    /// <summary>
    /// Guided recovery when InspectStorage reports IncompleteOrUnsupported.
    /// Never silently deletes the broken file; restores only verified backups.
    /// </summary>
    private static bool TryRecoverIncompleteVault()
    {
        var recovery = new VaultRecoveryWindow();
        if (recovery.ShowDialog() != true || recovery.Choice == VaultRecoveryWindow.RecoveryChoice.Cancel)
            return false;
        if (recovery.Choice == VaultRecoveryWindow.RecoveryChoice.StartFresh)
        {
            try { return TryStartFreshVault(VaultDatabase.Current); }
            catch (Exception error)
            {
                StartupDiagnostics.Record(StartupDiagnostics.Stage.VaultRecoveryFailed, error);
                MessageBox.Show("Fresh setup was not started. Existing material was not deleted. " +
                    "Keep the live database and all recovery files together; preservation or rollback may be incomplete. " +
                    "Check the startup diagnostic log before retrying.",
                    "Recovery needs attention", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        var newest = VaultRecoveryService.ListSqliteRecoveryCopies().FirstOrDefault();
        if (recovery.Choice == VaultRecoveryWindow.RecoveryChoice.RestoreSqlite)
        {
            var pick = newest;
            if (pick is null ||
                MessageBox.Show(
                    "Restore this recovery file?\n\n" + pick +
                    "\n\nThe broken vault will be moved aside first (not deleted).",
                    "Restore SQLite backup", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Choose a LaunchFlow SQLite recovery copy",
                    Filter = "SQLite vault (*.db)|*.db|All files (*.*)|*.*",
                    InitialDirectory = VaultRecoveryService.RecoveryDirectory
                };
                if (dialog.ShowDialog() != true) return false;
                pick = dialog.FileName;
            }

            try
            {
                var quarantine = VaultRecoveryService.RestoreSqliteBackup(pick);
                MessageBox.Show(
                    "Vault file restored. Unlock with your master password next.\n\n" +
                    "Previous broken file kept at:\n" + quarantine,
                    "Vault restored", MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }
            catch (Exception ex)
            {
                CrashLog.Write("VaultRestore", ex);
                MessageBox.Show(
                    "Could not restore that backup. Your broken vault was left in place when possible.\n\n" +
                    RunIssueNotice.SafeExceptionReason(ex),
                    "Restore failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        // Portable .launchflow import → quarantine broken → Initialize new vault.
        var exportDialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a LaunchFlow portable backup",
            Filter = "LaunchFlow file (*.launchflow)|*.launchflow"
        };
        if (exportDialog.ShowDialog() != true) return false;

        var exportPassword = new SecretPromptWindow("Portable backup password",
            "Enter the password used when this .launchflow file was exported.");
        if (exportPassword.ShowDialog() != true) return false;

        PortableExport.PortablePackage package;
        try { package = SecureTransferService.Read(exportDialog.FileName, exportPassword.Secret); }
        catch (Exception ex)
        {
            MessageBox.Show("Could not read that portable backup: " + RunIssueNotice.SafeExceptionReason(ex),
                "Import failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var master = new SecretPromptWindow("New vault master password",
            $"Choose a new master password (at least {PasswordStrength.MinimumLength} characters). " +
            "This unlocks LaunchFlow on this PC after the broken vault is moved aside.");
        master.RequireStrongPassword = true;
        if (master.ShowDialog() != true) return false;
        if (!PasswordStrength.MeetsMinimum(master.Secret))
        {
            MessageBox.Show(PasswordStrength.RejectionNotice(PasswordStrength.Evaluate(master.Secret)),
                "Choose a stronger password", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var confirm = new SecretPromptWindow("Confirm new master password",
            "Enter the same new master password again.");
        if (confirm.ShowDialog() != true || confirm.Secret != master.Secret)
        {
            MessageBox.Show("Passwords did not match.", "Import canceled",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        try
        {
            var quarantine = VaultRecoveryService.QuarantineLiveVault();
            var localDefaults = new Configuration();
            var projected = ImportConfigurationProjection.ForThisDevice(
                package.Config, localDefaults, localDefaults.GlobalShortcut);
            foreach (var workflow in projected.Workflows)
                workflow.Enabled = false;
            if (!TryCreateAndVerifyVault(VaultDatabase.Current, master.Secret, projected, package.Credentials))
                return false;
            MessageBox.Show(
                "Portable backup imported into a new vault. Automatic website launches are disabled until you review them.\n\n" +
                "Broken vault kept at:\n" + quarantine,
                "Vault recovered", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("VaultPortableRestore", ex);
            MessageBox.Show(
                "Could not import the portable backup after quarantining the broken file.\n\n" + RunIssueNotice.SafeExceptionReason(ex),
                "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private static bool InitializeVault()
    {
        // After a "forgot password" reset, fall through to first-run setup.
        for (int round = 0; round < 3; round++)
        {
            var db = VaultDatabase.Current;
            if (VaultRecoveryService.HasIncompletePreservation(db.DataDirectory))
            {
                StartupDiagnostics.Record(StartupDiagnostics.Stage.VaultRecoveryFailed);
                MessageBox.Show("A previous vault-preservation operation did not finish. " +
                    "LaunchFlow will not inspect, unlock or create a vault until the retained live and recovery sets " +
                    "have been checked together. Do not delete the preservation marker or any database companions " +
                    "to force setup; retain them for manual recovery.",
                    "Vault preservation needs attention", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            var diskState = db.InspectStorage();
            // A prior creation may have committed encrypted data to a staging
            // directory even though the live vault path is absent. Do not offer
            // ordinary broken-DB quarantine or an automatic fresh start: those
            // actions are for a *live* incomplete DB, not unclassified retained
            // creation material.
            if (!db.Exists && diskState == VaultStorageState.IncompleteOrUnsupported)
            {
                // Both retained staging directories and orphaned SQLite -wal/-shm
                // sidecars are unknown encrypted material. The ordinary live-DB
                // recovery path cannot inspect a missing main file.
                var retainedCreation = db.HasRetainedCreationStage();
                StartupDiagnostics.Record(StartupDiagnostics.Stage.VaultRecoveryStarted);
                MessageBox.Show(
                    retainedCreation
                        ? "An interrupted vault creation left retained staging files."
                        : "LaunchFlow found SQLite companion files without a main vault database.",
                    "Vault recovery requires preservation", MessageBoxButton.OK, MessageBoxImage.Warning);
                MessageBox.Show(
                    "No files were deleted or replaced. Preserve the entire %LOCALAPPDATA%\\LaunchFlow folder, " +
                    "including hidden .launchflow.db.create-* directories and -wal/-shm companions, as one set. " +
                    "Get help to inspect/recover the preserved material before creating a new vault; " +
                    "do not delete these files to force setup.",
                    "Preserve encrypted material", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            if (diskState == VaultStorageState.IncompleteOrUnsupported)
            {
                if (!TryRecoverIncompleteVault())
                    return false;
                // Portable import initializes+unlocks immediately; SQLite restore still needs Unlock.
                if (VaultDatabase.Current.IsUnlocked)
                {
                    try { ProfileVaultService.UnsealAll(); }
                    catch (Exception ex)
                    {
                        CrashLog.Write("ProfileUnseal", ex);
                        MessageBox.Show("Some browser sessions could not be restored; encrypted files were retained.",
                            "Browser sessions need attention", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    return true;
                }
                continue; // re-inspect after SQLite restore
            }
            if (diskState == VaultStorageState.LegacyUpgradeRequired)
            {
                var choice = MessageBox.Show(
                    "Your existing encrypted vault requires a security-format upgrade before LaunchFlow can open it. " +
                    "A verified recovery copy will be made before conversion. Older LaunchFlow versions will not " +
                    "open the upgraded vault. Existing automatic website launches will be disabled until you review " +
                    "their saved credentials. This upgrade cannot be undone in place.\\n\\n" +
                    "Keep the recovery copy and use an older compatible version only with a separate restored copy. " +
                    "Do you want to proceed?",
                    "Security upgrade required", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (choice != MessageBoxResult.Yes) return false;

                var prompt = new SecretPromptWindow("Upgrade encrypted vault",
                    "Enter your existing vault master password. LaunchFlow will verify your complete encrypted recovery copy before changing the vault.");
                if (prompt.ShowDialog() != true) return false;
                try
                {
                    var backup = db.UpgradeLegacyVault(prompt.Secret);
                    if (!db.IsUnlocked)
                        throw new InvalidOperationException("Vault upgrade completed but the session was revoked.");
                    MessageBox.Show(
                        "Vault security upgrade completed. A verified legacy recovery copy was retained at:\\n" +
                        backup + "\\n\\nReview saved website credentials before enabling automatic launches.",
                        "Security upgrade completed", MessageBoxButton.OK, MessageBoxImage.Information);
                    try { ProfileVaultService.UnsealAll(); }
                    catch (Exception ex)
                    {
                        CrashLog.Write("ProfileUnseal", ex);
                        MessageBox.Show("Some browser sessions could not be restored; encrypted files were retained.",
                            "Browser sessions need attention", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    CrashLog.Write("VaultUpgrade", ex);
                    MessageBox.Show(
                        ex is UnauthorizedAccessException
                            ? "The existing master password was not accepted. Your vault is unchanged."
                            : "The security upgrade could not be completed. Do not delete the existing vault or any " +
                              "recovery snapshots. No automatic reset will be attempted. Check local diagnostics " +
                              "and retain the encrypted files before retrying.",
                        "Security upgrade not completed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            if (diskState == VaultStorageState.PresentNeedsAuthentication)
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    var dialog = new UnlockWindow();
                    if (dialog.ShowDialog() != true)
                    {
                        if (dialog.ResetRequested)
                        {
                            // Cold start: no MainWindow browsers yet; wipe then retry for setup.
                            try { VaultResetService.ResetLocalVault(wipeProfiles: true); }
                            catch (Exception ex)
                            {
                                CrashLog.Write("VaultReset", ex);
                                MessageBox.Show(
                                    "Reset is incomplete. Some vault or profile files may still remain. Close other apps using those files and retry.",
                                    "Reset incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
                                return false;
                            }
                            break; // vault deleted; retry outer loop for setup
                        }
                        return false;
                    }
                    if (string.IsNullOrEmpty(dialog.Password)) continue;
                    if (db.Unlock(dialog.Password))
                    {
                        // Hello returns a recovered secret through the same Password
                        // property; only a successful typed unlock refreshes the timer.
                        if (!dialog.UsedWindowsHello)
                            WindowsHelloService.MarkPasswordVerified();
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
            var password = setup.ChosenPassword;
            var handoffOrdinalEqual = string.Equals(password, setup.TakeChosenPassword(), StringComparison.Ordinal);
            StartupDiagnostics.Record(StartupDiagnostics.Stage.SetupHandoff);
            StartupDiagnostics.RecordAdmission(StartupDiagnostics.Admission.HandoffOrdinalEqual, handoffOrdinalEqual);
            if (!handoffOrdinalEqual)
                throw new InvalidOperationException("The setup handoff changed. No vault was created.");
            var settings = new Configuration
            {
                StartWithWindows = setup.StartWithWindows,
                RunOnUnlock = setup.RunAfterUnlock,
                RunOnAppStart = setup.RunOnAppStart,
                MinimizeToTray = setup.RunInTray,
                VaultLockMinutes = 10,
                SealBrowserProfilesOnLock = true
            };
            try
            {
                if (!TryCreateAndVerifyVault(db, password, settings))
                {
                    MessageBox.Show(PasswordStrength.RejectionNotice(PasswordStrength.Evaluate(password)),
                        "Choose a stronger master password", MessageBoxButton.OK, MessageBoxImage.Warning);
                    continue;
                }
            }
            finally { password = ""; }
            try
            {
                // First-run setup (also after vault reset) must reconcile BOTH
                // startup mechanisms. Persisted preference was committed above;
                // an OS registration error must be disclosed, not hidden.
                new StartupRegistrationService(TaskSchedulerService.Apply, StartupService.Apply)
                    .Apply(settings.StartWithWindows, settings.PreferTaskSchedulerAutostart);
            }
            catch (Exception ex)
            {
                CrashLog.Write("StartupRegistrationSetup", ex);
                MessageBox.Show(
                    "Your vault settings were saved, but Windows startup registration could not be fully updated. " +
                    "Check Settings → Start with Windows after opening LaunchFlow.",
                    "Windows startup needs attention", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return true;
        }
        return false;
    }

    internal void KeepAliveForVaultResetSetup() =>
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

    /// <summary>
    /// Restores normal shutdown after an aborted vault-reset path that never
    /// reached <see cref="ContinueVaultResetSetup"/>.
    /// </summary>
    internal void CancelVaultResetKeepAlive()
    {
        if (ShutdownMode == ShutdownMode.OnExplicitShutdown)
            ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

    internal void ContinueVaultResetSetup()
    {
        try
        {
            if (!InitializeVault())
            {
                Shutdown();
                return;
            }
            var window = new MainWindow();
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }
        catch (Exception ex)
        {
            // Fire-and-forget reset handoff must not leave OnExplicitShutdown
            // with no window while holding the single-instance mutex.
            CrashLog.Write("ContinueVaultResetSetup", ex);
            try
            {
                MessageBox.Show(
                    "LaunchFlow could not finish setup after vault reset. The app will close; start LaunchFlow again to continue.",
                    "Setup interrupted", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* ignore */ }
            Shutdown();
        }
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
        {
            window.RestoreFromExternalLaunch();
            return;
        }

        // A second launch can arrive while the first instance is displaying
        // first-run setup or the master-password unlock dialog. Those dialogs
        // precede MainWindow, so SHOW must restore the active startup window
        // instead of silently waiting for a MainWindow that does not exist yet.
        var dialog = Windows.OfType<Window>().LastOrDefault(w => w.IsVisible);
        if (dialog is not null)
        {
            if (dialog.WindowState == WindowState.Minimized)
                dialog.WindowState = WindowState.Normal;
            dialog.Show();
            dialog.Activate();
            var previousTopmost = dialog.Topmost;
            dialog.Topmost = true;
            dialog.Topmost = previousTopmost;
            return;
        }

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
        StartupDiagnostics.Record(StartupDiagnostics.Stage.ProcessExited);
        _listenerCancellation?.Cancel();
        VaultDatabase.Current.Lock();
        // The listener observes cancellation and disposes its pipe. No blocking
        // wait here: exit should never freeze if a client process is misbehaving.
        try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
