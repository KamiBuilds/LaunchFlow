using System.Text;
using LaunchFlow.Models;
using LaunchFlow.Services;

internal static class PrivacyTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("diagnostics never persist arbitrary alphanumeric categories or messages", () =>
        {
            File.Delete(CrashLog.LogPath);
            CrashLog.Write("PrivateCategoryToken991", "PrivateSecretToken992");
            var log = File.ReadAllText(CrashLog.LogPath);
            Check(!log.Contains("PrivateCategoryToken991", StringComparison.Ordinal));
            Check(!log.Contains("PrivateSecretToken992", StringComparison.Ordinal));
            Check(log.Contains("[General] Event", StringComparison.Ordinal));
        });
        test("diagnostics drop exception messages stacks type names URLs paths and inner errors", () =>
        {
            File.Delete(CrashLog.LogPath);
            Exception error;
            try { throw new PrivateExceptionTypeToken993("https://private.test/token?secret=Token994 /private/vault/Token995", new Exception("InnerToken996")); }
            catch (Exception e) { error = e; }
            CrashLog.Write("UI", error);
            var log = File.ReadAllText(CrashLog.LogPath);
            Check(!new[] { "PrivateExceptionTypeToken993", "private.test", "Token994", "/private", "Token995", "InnerToken996", "PrivacyTests", " at " }.Any(log.Contains));
            Check(log.Contains("[UI] Failure", StringComparison.Ordinal));
        });
        test("diagnostics do not evaluate exception properties", () =>
        {
            File.Delete(CrashLog.LogPath);
            CrashLog.Write("Fatal", new HostileException());
            Check(File.ReadAllText(CrashLog.LogPath).Contains("[Fatal] Failure", StringComparison.Ordinal));
        });
        test("diagnostics strip newlines and arbitrary message text", () =>
        {
            File.Delete(CrashLog.LogPath);
            CrashLog.Write("UI\nforged", "secret\r\nforged");
            Check(File.ReadAllLines(CrashLog.LogPath).Length == 1);
            Check(File.ReadAllText(CrashLog.LogPath).Contains("[General] Event", StringComparison.Ordinal));
        });
        test("diagnostic rotation is bounded to current and one previous file", () =>
        {
            File.WriteAllText(CrashLog.LogPath, new string('x', 512 * 1024 - 2));
            CrashLog.Write("UpdateCheck", "Token997");
            Check(new FileInfo(CrashLog.LogPath).Length <= 512 * 1024);
            Check(File.Exists(CrashLog.LogPath + ".previous"));
            File.WriteAllText(CrashLog.LogPath, new string('y', 512 * 1024));
            Parallel.For(0, 30, _ => CrashLog.Write("UpdateCheck", "Token997"));
            Check(File.ReadAllLines(CrashLog.LogPath).Length == 30);
            Check(new FileInfo(CrashLog.LogPath + ".previous").Length == 512 * 1024);
        });
        test("diagnostic IO failures never throw", () =>
        {
            File.Delete(CrashLog.LogPath);
            Directory.CreateDirectory(CrashLog.LogPath);
            try { CrashLog.Write("UI", new Exception("Token998")); }
            finally { Directory.Delete(CrashLog.LogPath); }
        });
        foreach (var type in new[] { "Navigate", "Click", "Fill", "Wait", "Press", "UnknownPrivateType999" })
            test("WorkflowStep ToString reveals only a fixed action label: " + type, () =>
            {
                var step = new WorkflowStep { Type = type, Target = "https://private.test/Token1000", Value = "PrivateValue1001", Description = "PrivateDescription1002" };
                var text = step.ToString();
                Check(!text.Contains("private", StringComparison.OrdinalIgnoreCase) && !text.Contains("Token1000", StringComparison.Ordinal));
                Check(text == (type == "UnknownPrivateType999" ? "Workflow step" : type));
            });
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Assertion failed"); }
    private sealed class PrivateExceptionTypeToken993(string message, Exception inner) : Exception(message, inner);
    private sealed class HostileException : Exception
    {
        public override string Message => throw new InvalidOperationException("Do not evaluate arbitrary exception properties");
        public override string? StackTrace => throw new InvalidOperationException("Do not evaluate arbitrary exception properties");
    }
}
