// Inert OS/UI seams: fixture bytes are checked, never executed.
namespace Microsoft.Win32
{
    internal static class Registry
    {
        internal static string? InstalledVersion;
        internal static RegistryKey CurrentUser { get; } = new();
    }
    internal sealed class RegistryKey : IDisposable
    {
        public RegistryKey? OpenSubKey(string name) => this;
        public object? GetValue(string name) => name == "DisplayVersion" ? Registry.InstalledVersion : null;
        public void Dispose() { }
    }
}
namespace LaunchFlow.UniversalInstaller
{
    // Name resolution shadows System.Diagnostics.Process only in this disposable harness.
    internal sealed class Process : IDisposable
    {
        public static TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static TaskCompletionSource ChildExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static int StartCalls;
        public static string? LastPath;
        public static void Reset()
        {
            StartCalls = 0; LastPath = null;
            Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ChildExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public int ExitCode => 0;
        public static Process Start(System.Diagnostics.ProcessStartInfo info)
        {
            if (!File.ReadAllBytes(info.FileName).SequenceEqual(new byte[] { 1, 2, 3 })) throw new Exception("Only inert fixture bytes allowed");
            StartCalls++; LastPath = info.FileName; Started.SetResult(); return new();
        }
        public Task WaitForExitAsync() => ChildExit.Task;
        public void Dispose() { }
    }
    internal enum FormBorderStyle { FixedSingle }
    internal enum FormStartPosition { CenterScreen }
    internal enum FlatStyle { Flat }
    internal enum MessageBoxButtons { YesNo, OK }
    internal enum MessageBoxIcon { Question, Warning }
    internal enum DialogResult { Yes }
    internal static class MessageBox { public static DialogResult Show(Form form, string message, string title, MessageBoxButtons buttons, MessageBoxIcon icon) => DialogResult.Yes; }
    internal static class ApplicationConfiguration { public static void Initialize() { } }
    internal static class Application { public static void Run(Form form) => throw new Exception("Native UI activation forbidden"); }
    internal static class Cursors { public static object Hand { get; } = new(); }
    internal enum FontStyle { Bold }
    internal sealed class Font
    {
        public Font(string family, int size) { }
        public Font(string family, int size, FontStyle style) { }
    }
    internal class Control
    {
        public string Text { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled { get; set; } = true;
        public System.Drawing.Color BackColor { get; set; }
        public System.Drawing.Color ForeColor { get; set; }
        public Font? Font { get; set; }
        public object? Cursor { get; set; }
        public void SetBounds(int x, int y, int width, int height) { }
        public event EventHandler? Click;
    }
    internal sealed class Label : Control { }
    internal sealed class ProgressBar : Control { public int Value { get; set; } }
    internal sealed class Button : Control { public FlatStyle FlatStyle { get; set; } public FlatAppearance FlatAppearance { get; } = new(); }
    internal sealed class FlatAppearance { public int BorderSize { get; set; } }
    internal sealed class ControlCollection { public void AddRange(Control[] values) { } }
    internal sealed class ClosingArgs : EventArgs { public bool Cancel { get; set; } }
    internal class Form : Control
    {
        public System.Drawing.Size ClientSize { get; set; }
        public System.Drawing.Size MinimumSize { get; set; }
        public System.Drawing.Size MaximumSize { get; set; }
        public System.Drawing.Size Size { get; set; }
        public FormBorderStyle FormBorderStyle { get; set; }
        public FormStartPosition StartPosition { get; set; }
        public bool MaximizeBox { get; set; }
        public ControlCollection Controls { get; } = new();
        public event EventHandler? Shown;
        public event EventHandler? FormClosed;
        public event EventHandler<ClosingArgs>? FormClosing;
        public void RaiseClosing(ClosingArgs args) => FormClosing?.Invoke(this, args);
    }
}
