using System.Diagnostics;
using System.IO;

namespace LaunchFlow.Services;

/// <summary>
/// Registers a per-user Scheduled Task at logon as a more reliable alternative
/// to the Run key on some managed PCs.
/// </summary>
public static class TaskSchedulerService
{
    public const string TaskName = "LaunchFlow Autostart";
    private static readonly object Gate = new();
    private static Process? _activeChild;

    public static void Apply(bool enabled)
    {
        var exe = Environment.ProcessPath
                   ?? Process.GetCurrentProcess().MainModule?.FileName
                   ?? throw new InvalidOperationException("Cannot resolve LaunchFlow.exe path.");

        lock (Gate)
        {
            // Own prior children before another mutation (PLAT-03).
            ReapActiveChild();
            // Locale-independent absence: query first, skip delete when missing (PLAT96-01).
            if (IsRegisteredUnlocked())
                DeleteExistingTaskUnlocked();

            if (!enabled) return;

            var create =
                $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --tray\" " +
                "/SC ONLOGON /RL LIMITED /F";
            RunSchTasksUnlocked(create);
        }
    }

    public static bool IsRegistered()
    {
        lock (Gate) return IsRegisteredUnlocked();
    }

    private static bool IsRegisteredUnlocked()
    {
        try
        {
            var result = RunSchTasksUnlocked($"/Query /TN \"{TaskName}\"", throwOnFailure: false);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteExistingTaskUnlocked()
    {
        var result = RunSchTasksUnlocked($"/Delete /TN \"{TaskName}\" /F", throwOnFailure: false);
        if (result.ExitCode == 0) return;
        // Race: removed between query and delete — treat as absent.
        if (!IsRegisteredUnlocked()) return;
        throw Failure(result);
    }

    private sealed record SchTasksResult(int ExitCode, string Output, string Error);

    private static SchTasksResult RunSchTasksUnlocked(string args, bool throwOnFailure = true)
    {
        ReapActiveChild();
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var p = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start schtasks.exe.");
        _activeChild = p;
        try
        {
            if (!p.WaitForExit(15_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                try { p.WaitForExit(5_000); } catch { /* ignore */ }
                throw new TimeoutException("Windows Task Scheduler did not finish the LaunchFlow request.");
            }
            var result = new SchTasksResult(p.ExitCode, p.StandardOutput.ReadToEnd(), p.StandardError.ReadToEnd());
            if (throwOnFailure && result.ExitCode != 0) throw Failure(result);
            return result;
        }
        finally
        {
            if (ReferenceEquals(_activeChild, p))
                _activeChild = null;
            p.Dispose();
        }
    }

    private static void ReapActiveChild()
    {
        var prior = _activeChild;
        if (prior is null) return;
        try
        {
            if (!prior.HasExited)
            {
                try { prior.Kill(entireProcessTree: true); } catch { /* best effort */ }
                try { prior.WaitForExit(5_000); } catch { /* ignore */ }
            }
        }
        finally
        {
            try { prior.Dispose(); } catch { /* ignore */ }
            if (ReferenceEquals(_activeChild, prior))
                _activeChild = null;
        }
    }

    private static InvalidOperationException Failure(SchTasksResult result) =>
        new("Could not update the Windows Scheduled Task for LaunchFlow. " +
            (string.IsNullOrWhiteSpace(result.Error) ? $"Exit code {result.ExitCode}." : result.Error.Trim()));
}
