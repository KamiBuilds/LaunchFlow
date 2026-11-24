namespace Microsoft.Win32
{
    public sealed class OpenFileDialog
    {
        public string Title { get; set; } = "";
        public string Filter { get; set; } = "";
        public string InitialDirectory { get; set; } = "";
        public static Queue<string?> Paths { get; } = new();
        public string FileName { get; private set; } = "";
        public bool? ShowDialog() { var path = Paths.Count == 0 ? null : Paths.Dequeue(); FileName = path ?? ""; return path is not null; }
    }
}
namespace LaunchFlow
{
    public sealed class SecretPromptWindow
    {
        public static Queue<string?> Submissions { get; } = new();
        public System.Windows.Window? Owner { get; set; }
        public string Secret { get; private set; } = "";
        public SecretPromptWindow(string title, string text) { _ = title; _ = text; }
        public bool? ShowDialog() { var secret = Submissions.Count == 0 ? null : Submissions.Dequeue(); Secret = secret ?? ""; return secret is not null; }
    }
}
namespace LaunchFlow.Services
{
    public static class RunIssueNotice { public static string SafeExceptionReason(Exception _) => "Synthetic safe error category"; }
}
