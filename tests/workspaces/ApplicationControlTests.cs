using LaunchFlow.Services;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class ApplicationControlTests
{
    public static IEnumerable<(string Name, Action Body)> Cases()
    {
        yield return ("successful dependency preflight does not touch vault", () =>
        {
            int invoked = 0;
            var result = WindowsApplicationControl.RunProbe(() => invoked++);
            Check(result.Ready && invoked == 1 && result.Message == "", "valid runtime falsely rejected");
        });
        yield return ("application control 0x800711C7 is detected through invocation wrappers", () =>
        {
            var blocked = new FileLoadException("An Application Control policy has blocked this file.");
            var wrapped = new TargetInvocationException(new COMException("Blocked", unchecked((int)0x800711C7)));
            Check(WindowsApplicationControl.HasApplicationControlFailure(wrapped), "nested policy failure was missed");
            var result = WindowsApplicationControl.RunProbe(() => throw wrapped);
            Check(result.Reason == WindowsApplicationControl.Failure.ApplicationControl &&
                result.Message.Contains("administrator") &&
                result.Message.Contains("CodeIntegrity") &&
                !result.Message.Contains("disable Windows security", StringComparison.OrdinalIgnoreCase),
                "policy warning must help administrator without suggesting a security bypass");
            Check(WindowsApplicationControl.HasApplicationControlFailure(blocked),
                "file-load error containing explicit policy block was missed");
        });
        yield return ("missing SQLite DLL reports incomplete install before new master password", () =>
        {
            var result = WindowsApplicationControl.RunProbe(
                () => throw new FileNotFoundException("Missing Microsoft.Data.Sqlite.dll"));
            Check(result.Reason == WindowsApplicationControl.Failure.MissingComponent &&
                result.Message.Contains("No vault was opened"), "missing dependency reported incorrectly");
        });
        yield return ("unexpected native load failure reports error without secrets", () =>
        {
            var result = WindowsApplicationControl.RunProbe(
                () => throw new InvalidOperationException("user-password-is-not-for-diagnostics"));
            Check(result.Reason == WindowsApplicationControl.Failure.DependencyError &&
                !result.Message.Contains("user-password-is-not-for-diagnostics"),
                "preflight error leaked exception contents");
        });
    }
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
