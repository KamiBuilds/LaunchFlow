// Minimal UI seam: tests execute the real window controller on Linux, not WPF rendering.
namespace System.Windows
{
    public class RoutedEventArgs : EventArgs { }
    public enum MessageBoxButton { OK }
    public enum MessageBoxImage { Warning, Information }
    public static class MessageBox
    {
        public static void Show(object owner, string message, string title, MessageBoxButton buttons, MessageBoxImage image) { }
    }
    public class Window
    {
        public Window? Owner { get; set; }
        public event EventHandler? Loaded;
        public event EventHandler? Closed;
        public void Close() => Closed?.Invoke(this, EventArgs.Empty);
        protected void RaiseLoaded() => Loaded?.Invoke(this, EventArgs.Empty);
    }
}
namespace System.Windows.Threading
{
    public class DispatcherTimer
    {
        public TimeSpan Interval { get; set; }
        public event EventHandler? Tick;
        public void Start() { }
        public void Stop() { }
        public void Fire() => Tick?.Invoke(this, EventArgs.Empty);
    }
}
namespace LaunchFlow.Services
{
    public static class DarkWindowChrome { public static void Apply(object window) { } }
    public sealed class CredentialVault
    {
        public Models.SiteCredential? Read(Models.WebsiteWorkflow workflow, Models.Configuration? configuration, string? master) =>
            throw new NotSupportedException("Recorder replay tests must not request real credentials.");
    }
    public static class ProfileVaultService
    {
        public static string SealedRoot => throw new NotSupportedException("Recorder replay tests must not open persistent browser profiles.");
    }
    public static class MonitorService
    {
        public sealed record Bounds(int Left, int Top, int Width, int Height);
        public sealed record Monitor(Bounds Bounds);
        public static List<Monitor> GetMonitors() => throw new NotSupportedException("Recorder replay tests must not position native windows.");
    }
}
namespace LaunchFlow
{
    public sealed class TestControl
    {
        public object? Content { get; set; }
        public string? Text { get; set; }
        public bool IsEnabled { get; set; } = true;
        public object? SelectedItem { get; set; }
        private object? _itemsSource;
        public object? ItemsSource { get => _itemsSource; set { _itemsSource = value; SelectedItem = null; } }
        public void ScrollIntoView(object item) { }
    }
    public class StepEditorWindow : System.Windows.Window
    {
        public Models.WorkflowStep Result { get; }
        public StepEditorWindow(Models.WorkflowStep step) => Result = step;
        public bool? ShowDialog() => false;
    }
    public partial class VisualRecorderWindow
    {
        private readonly TestControl CaptureToggle = new();
        private readonly TestControl CaptureStatus = new();
        private readonly TestControl StepList = new();
        private readonly TestControl SaveStepsButton = new();
        private void InitializeComponent() => Closed += Window_Closed;
        public void TestLoad() => RaiseLoaded();
        public void TestSave() => Save_Click(this, new());
        public void TestCancel() => Cancel_Click(this, new());
        public void TestToggle() => ToggleCapture_Click(this, new());
        public void TestSelectFirst() => StepList.SelectedItem = RecordedSteps.FirstOrDefault();
        public void TestRemove() => Remove_Click(this, new());
        public void TestTick() => _drainTimer.Fire();
        public void TestPauseWithoutDrain() => _recorder.Pause();
        public void TestClear() => Clear_Click(this, new());
        public void TestCaptureUrl() => CaptureUrl_Click(this, new());
        public Task TestWaitForUpdatesAsync() => _recorder.SynchronizeAsync();
        public bool TestToggleEnabled => CaptureToggle.IsEnabled;
        public string? TestStatus => CaptureStatus.Text;
    }
}
