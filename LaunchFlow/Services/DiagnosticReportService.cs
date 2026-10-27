using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace LaunchFlow.Services;

public static class DiagnosticReportService
{
    private static readonly Regex SafeEvent = new(
        @"\A\d{4}-\d{2}-\d{2}T[0-9:.+-]+\s+\[[A-Za-z_-]+\]\s+[A-Za-z_-]+\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void Export(string path)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        var report = new StringBuilder();
        report.AppendLine("LaunchFlow local diagnostic report");
        report.AppendLine("Version: " + version);
        report.AppendLine("Generated (UTC): " + DateTimeOffset.UtcNow.ToString("O"));
        report.AppendLine("Platform: Windows");
        report.AppendLine();
        report.AppendLine("Redacted event codes:");

        if (File.Exists(CrashLog.LogPath))
        {
            foreach (var line in File.ReadLines(CrashLog.LogPath).TakeLast(250))
            {
                if (line.Length <= 180 && SafeEvent.IsMatch(line.Trim()))
                    report.AppendLine(line.Trim());
            }
        }

        // Never include workflow configuration, URLs, credential values or raw exceptions.
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
    }
}
