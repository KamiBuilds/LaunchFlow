using System.Diagnostics;
using System.IO;
using System.Text;

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

    private enum QueryOutcome
    {
        Present,
        Absent,
        UnknownError
    }

    public static void Apply(bool enabled)
    {
        var exe = Environment.ProcessPath
                   ?? Process.GetCurrentProcess().MainModule?.FileName
                   ?? throw new InvalidOperationException("Cannot resolve LaunchFlow.exe path.");

        lock (Gate)
        {
            ReapActiveChild();
            var presence = QueryUnlocked();
            if (presence == QueryOutcome.UnknownError)
                throw new InvalidOperationException(
                    "Could not query the Windows Scheduled Task for LaunchFlow. Autostart was not changed.");
            if (presence == QueryOutcome.Present)
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
        lock (Gate) return QueryUnlocked() == QueryOutcome.Present;
    }

    private static QueryOutcome QueryUnlocked()
    {
        try
        {
            var named = RunSchTasksUnlocked($"/Query /TN \"{TaskName}\"", throwOnFailure: false);
            if (named.ExitCode == 0) return QueryOutcome.Present;

            // Exit code 1 is ambiguous (missing vs access denied). Confirm via a
            // full CSV task list and look for the exact task name — no English phrases.
            var list = RunSchTasksUnlocked("/Query /FO CSV /NH", throwOnFailure: false);
            if (list.ExitCode != 0)
                return QueryOutcome.UnknownError;
            return CsvListContainsTask(list.Output) ? QueryOutcome.Present : QueryOutcome.Absent;
        }
        catch (TimeoutException)
        {
            return QueryOutcome.UnknownError;
        }
        catch (InvalidOperationException)
        {
            return QueryOutcome.UnknownError;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return QueryOutcome.UnknownError;
        }
    }

    private static bool CsvListContainsTask(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return false;
        using var reader = new StringReader(csv);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            // schtasks CSV: "TaskName","Next Run Time","Status",...
            var name = ReadCsvFirstField(line);
            if (string.Equals(name, TaskName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, @"\" + TaskName, StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(@"\" + TaskName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string ReadCsvFirstField(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return string.Empty;
        if (line[0] != '"')
        {
            var comma = line.IndexOf(',');
            return comma < 0 ? line.Trim() : line[..comma].Trim();
        }
        var sb = new StringBuilder();
        for (var i = 1; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                    continue;
                }
                break;
            }
            sb.Append(line[i]);
        }
        return sb.ToString().Trim();
    }

    private static void DeleteExistingTaskUnlocked()
    {
        var result = RunSchTasksUnlocked($"/Delete /TN \"{TaskName}\" /F", throwOnFailure: false);
        if (result.ExitCode == 0)
        {
            if (QueryUnlocked() == QueryOutcome.Present)
                throw new InvalidOperationException(
                    "Windows Scheduled Task delete reported success but the task is still present.");
            return;
        }
        var after = QueryUnlocked();
        if (after == QueryOutcome.Absent) return;
        if (after == QueryOutcome.UnknownError)
            throw new InvalidOperationException(
                "Could not verify removal of the Windows Scheduled Task for LaunchFlow.");
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
