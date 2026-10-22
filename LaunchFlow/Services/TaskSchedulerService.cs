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
        RunSchTasks($"/Delete /TN \"{TaskName}\" /F", ignoreErrors: true);

        if (!enabled) return;

        var create =
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --tray\" " +
            "/SC ONLOGON /RL LIMITED /F";
        RunSchTasks(create, ignoreErrors: false);
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

    private static void RunSchTasks(string args, bool ignoreErrors)
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
        p.WaitForExit(15_000);
        if (!ignoreErrors && p.ExitCode != 0)
        {
            var err = p.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                "Could not update the Windows Scheduled Task for LaunchFlow. " +
                (string.IsNullOrWhiteSpace(err) ? $"Exit code {p.ExitCode}." : err.Trim()));
        }
    }
}
