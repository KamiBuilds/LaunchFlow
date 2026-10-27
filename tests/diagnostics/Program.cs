using LaunchFlow.Services;

var root = Path.Combine(Path.GetTempPath(), "launchflow-diag-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    CrashLog.LogPath = Path.Combine(root, "diagnostics.log");
    File.WriteAllLines(CrashLog.LogPath,
    [
        "2026-10-27T07:00:00+00:00 [CloseSessions] Failure",
        "2026-10-27T07:00:01+00:00 [ProfileSeal] Event",
        "Private URL https://internal.example.test/?token=synthetic-secret",
        "2026-10-27T07:00:03+00:00 [Browser] synthetic-secret"
    ]);
    var output = Path.Combine(root, "report.txt");
    DiagnosticReportService.Export(output);
    var contents = File.ReadAllText(output);
    if (!contents.Contains("[CloseSessions] Failure", StringComparison.Ordinal) ||
        !contents.Contains("[ProfileSeal] Event", StringComparison.Ordinal) ||
        contents.Contains("synthetic-secret", StringComparison.Ordinal) ||
        contents.Contains("internal.example.test", StringComparison.Ordinal))
        throw new Exception("Diagnostic redaction regression.");
    Console.WriteLine("Redacted diagnostic report tests passed.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

namespace LaunchFlow.Services
{
    public static class CrashLog
    {
        public static string LogPath { get; set; } = "";
    }
}
