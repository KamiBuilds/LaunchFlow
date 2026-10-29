using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LaunchFlow.Services;

/// <summary>
/// Detects missing / organization-blocked SQLite runtime dependencies without
/// reading, creating or unlocking any LaunchFlow vault files. Policy failures
/// must be referred to the Windows security administrator, never bypassed.
/// </summary>
public static class WindowsApplicationControl
{
    public enum Failure { None, ApplicationControl, MissingComponent, DependencyError }

    public sealed record CheckResult(Failure Reason, string Message)
    {
        public bool Ready => Reason == Failure.None;
    }

    public static CheckResult CheckRequiredComponents() => RunProbe(ProbeSqliteMemory);

    // Exposed for deterministic tests: no real vault, disk or admin policy needed.
    public static CheckResult RunProbe(Action probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        try
        {
            probe();
            return new CheckResult(Failure.None, "");
        }
        catch (Exception ex)
        {
            var cause = Unwrap(ex);
            if (HasApplicationControlFailure(ex))
            {
                return new CheckResult(Failure.ApplicationControl,
                    "Windows Application Control blocked a component LaunchFlow needs, most likely Microsoft.Data.Sqlite.dll.\n\n" +
                    "LaunchFlow stopped BEFORE reading or creating the vault, so this check did not change your data.\n\n" +
                    "A managed PC requires its administrator to approve a trusted, correctly signed LaunchFlow build " +
                    "and its DLL dependencies. On a personal PC, Windows Smart App Control may also require a trusted build.\n\n" +
                    "Open Event Viewer > Applications and Services Logs > Microsoft > Windows > CodeIntegrity > Operational " +
                    "and have the administrator review the blocked-file event. " +
                    "Do not disable Windows security protections.\n\n" +
                    $"Error: 0x{unchecked((uint)cause.HResult):X8} ({cause.GetType().Name}).");
            }
            if (cause is FileNotFoundException or DllNotFoundException)
            {
                return new CheckResult(Failure.MissingComponent,
                    "LaunchFlow cannot load a required SQLite component from its installation.\n\n" +
                    "No vault was opened or created. Reinstall an approved, complete LaunchFlow build. " +
                    "If this PC is managed, ask your administrator to verify the installer and all DLL dependencies.\n\n" +
                    $"Diagnostic: {cause.GetType().Name}, 0x{unchecked((uint)cause.HResult):X8}.");
            }
            return new CheckResult(Failure.DependencyError,
                "LaunchFlow cannot initialize its required in-memory SQLite runtime.\n\n" +
                "No vault was opened or created by this compatibility check. " +
                "Contact the installer provider or administrator; do not reset the vault or create a new master password.\n\n" +
                $"Diagnostic: {cause.GetType().Name}, 0x{unchecked((uint)cause.HResult):X8}.");
        }
    }

    public static bool HasApplicationControlFailure(Exception exception)
    {
        foreach (var cause in Walk(exception))
        {
            // 0x800711C7 = ERROR_APPCONTROL_BLOCKED; 0x800704EC
            // = ERROR_ACCESS_DISABLED_BY_POLICY. Do not infer a bypass route.
            if (cause.HResult is unchecked((int)0x800711C7) or unchecked((int)0x800704EC))
                return true;
            if (cause is FileLoadException &&
                (cause.Message.Contains("Application Control policy", StringComparison.OrdinalIgnoreCase) ||
                 cause.Message.Contains("blocked this file", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static IEnumerable<Exception> Walk(Exception exception)
    {
        for (var node = exception; node is not null; node = node.InnerException!)
            yield return node;
    }

    private static Exception Unwrap(Exception exception)
    {
        var node = exception;
        while (node is TargetInvocationException or TypeInitializationException or AggregateException &&
               node.InnerException is not null)
            node = node.InnerException;
        return node;
    }

    private static void ProbeSqliteMemory()
    {
        // Reflection defers Microsoft.Data.Sqlite type resolution into this
        // try/catch boundary, so App Control blocks become actionable instead
        // of crashing after a user has already chosen a master password.
        var sqlite = Assembly.Load("Microsoft.Data.Sqlite");
        var type = sqlite.GetType("Microsoft.Data.Sqlite.SqliteConnection", throwOnError: true)!;
        using var connection = (IDisposable)(Activator.CreateInstance(type, "Data Source=:memory:") ??
            throw new InvalidOperationException("SQLite connection unavailable."));
        (type.GetMethod("Open", Type.EmptyTypes) ??
            throw new MissingMethodException("SQLite runtime has no Open method."))
            .Invoke(connection, null);
        // Opening an in-memory connection forces native SQLite runtime loading,
        // but never touches %LOCALAPPDATA%/LaunchFlow or an encrypted vault.
    }
}
