using System.IO;
using System.Diagnostics;
using System.Text;

namespace LaunchFlow.Services;

/// <summary>
/// Records startup checkpoints without saving exception messages, arguments,
/// URLs, credentials, environment variables or vault contents.
/// This diagnostic never reads or opens the encrypted vault database.
/// </summary>
internal static class StartupDiagnostics
{
    internal enum Stage
    {
        ProcessStarted,
        ExistingInstance,
        ActivationFailed,
        DependencyProbeStarted,
        DependencyProbeRejected,
        VaultUnlockStarted,
        VaultUnlockCancelled,
        VaultReady,
        WindowConstructionStarted,
        WindowShown,
        OptionalUiInitializationFailed,
        StartupException,
        UiUnhandledException,
        FatalException,
        ProcessExited
    }

    private static readonly object Gate = new();
    private const long MaximumBytes = 256 * 1024;

    internal static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LaunchFlow", "startup-diagnostics.log");

    internal static Exception? RootCause(Exception? exception)
    {
        var current = exception;
        for (var i = 0; i < 8 && current?.InnerException is not null; i++)
        {
            if (current is System.Reflection.TargetInvocationException or
                TypeInitializationException or System.Windows.Markup.XamlParseException)
                current = current.InnerException;
            else
                break;
        }
        return current;
    }

    internal static void Record(Stage stage, Exception? exception = null)
    {
        try
        {
            lock (Gate)
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // Exception type and HRESULT are stable diagnostics, unlike
                // exception.Message or StackTrace (which may contain secrets).
                var root = RootCause(exception);
                var type = root?.GetType().Name ?? "None";
                var code = root is null ? "none" : $"0x{unchecked((uint)root.HResult):X8}";
                // The assembly-owned method name (never the stack trace) helps
                // distinguish UI initialization, theme, browser and vault faults.
                var frame = root is null ? null : new StackTrace(root, false).GetFrames()?
                    .FirstOrDefault(f => f.GetMethod()?.DeclaringType?.FullName?
                        .StartsWith("LaunchFlow.", StringComparison.Ordinal) == true);
                var method = frame?.GetMethod();
                var location = method is null ? "unknown" :
                    $"{method.DeclaringType!.Name}.{method.Name}";
                var line = $"{DateTimeOffset.UtcNow:O} {stage} {type} {code} {location}{Environment.NewLine}";
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > MaximumBytes)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never prevent the app from starting or exiting.
        }
    }
}
