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

    public static void Apply(bool enabled)
    {
        var exe = Environment.ProcessPath
                   ?? Process.GetCurrentProcess().MainModule?.FileName
                   ?? throw new InvalidOperationException("Cannot resolve LaunchFlow.exe path.");

        // Remove any previous task first for a clean rewrite.
        DeleteExistingTask();

        if (!enabled) return;

        var create =
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --tray\" " +
            "/SC ONLOGON /RL LIMITED /F";
        RunSchTasks(create);
    }

    public static bool IsRegistered()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{TaskName}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(8000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteExistingTask()
    {
        var result = RunSchTasks($"/Delete /TN \"{TaskName}\" /F", throwOnFailure: false);
        if (result.ExitCode == 0) return;
        var detail = result.Error + "\n" + result.Output;
        if (detail.Contains("cannot find", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            return;
        throw Failure(result);
    }

    private sealed record SchTasksResult(int ExitCode, string Output, string Error);

    private static SchTasksResult RunSchTasks(string args, bool throwOnFailure = true)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi)
                      ?? throw new InvalidOperationException("Could not start schtasks.exe.");
        if (!p.WaitForExit(15_000))
            throw new TimeoutException("Windows Task Scheduler did not finish the LaunchFlow request.");
        var result = new SchTasksResult(p.ExitCode, p.StandardOutput.ReadToEnd(), p.StandardError.ReadToEnd());
        if (throwOnFailure && result.ExitCode != 0) throw Failure(result);
        return result;
    }

    private static InvalidOperationException Failure(SchTasksResult result) =>
        new("Could not update the Windows Scheduled Task for LaunchFlow. " +
            (string.IsNullOrWhiteSpace(result.Error) ? $"Exit code {result.ExitCode}." : result.Error.Trim()));
}
