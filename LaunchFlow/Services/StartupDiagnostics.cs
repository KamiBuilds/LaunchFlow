using System.IO;
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
                var type = exception?.GetType().Name ?? "None";
                var code = exception is null ? "none" : $"0x{unchecked((uint)exception.HResult):X8}";
                var line = $"{DateTimeOffset.UtcNow:O} {stage} {type} {code}{Environment.NewLine}";
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
