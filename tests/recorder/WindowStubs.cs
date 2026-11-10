// Minimal UI seam: tests execute the real window controller on Linux, not WPF rendering.
namespace System.Windows
{
    public class RoutedEventArgs : EventArgs { }
    public enum Visibility { Visible, Collapsed }
    public enum MessageBoxButton { OK, YesNo }
    public enum MessageBoxImage { Warning, Information, Question }
    public enum MessageBoxResult { Yes, No }
    public static class MessageBox
    {
        public static readonly List<string> Messages = new();
        public static MessageBoxResult Show(object owner, string message, string title, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult = MessageBoxResult.Yes)
        {
            Messages.Add(message);
            return MessageBoxResult.Yes;
        }
    }
    public class Window
    {
        public Window? Owner { get; set; }
        public bool Topmost { get; set; }
        public event EventHandler? Loaded;
        public event EventHandler? Closed;
        public void Close() => Closed?.Invoke(this, EventArgs.Empty);
        protected void RaiseLoaded() => Loaded?.Invoke(this, EventArgs.Empty);
        public object? FindResource(object key) => null;
    }
}
namespace System.Windows.Media
{
    public struct Color
    {
        public byte R, G, B, A;
        public static Color FromRgb(byte r, byte g, byte b) => new() { R = r, G = g, B = b, A = 255 };
    }
    public class SolidColorBrush
    {
        public Color Color { get; }
        public SolidColorBrush(Color color) => Color = color;
    }
    public class Brush { }
}
namespace System.Windows.Media.Animation
{
    public class Storyboard
    {
        public void Begin(object target, bool isControllable) { }
        public void Stop(object target) { }
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
}
namespace LaunchFlow
{
    public sealed class TestControl
    {
        public object? Content { get; set; }
        public string? Text { get; set; }
        public object? Background { get; set; }
        public object? BorderBrush { get; set; }
        public object? Fill { get; set; }
        public double Opacity { get; set; } = 1;
        public bool IsEnabled { get; set; } = true;
        public System.Windows.Visibility Visibility { get; set; } = System.Windows.Visibility.Visible;
        private object? _selectedItem;
        public List<object> SelectedItems { get; } = new();
        public object? SelectedItem
        {
            get => _selectedItem;
            set
            {
                _selectedItem = value;
                SelectedItems.Clear();
                if (value is not null) SelectedItems.Add(value);
            }
        }
        private object? _itemsSource;
        public object? ItemsSource { get => _itemsSource; set { _itemsSource = value; SelectedItem = null; } }
        public int SelectedIndex
        {
            get => ItemsSource is IList<Models.WorkflowStep> items && SelectedItem is Models.WorkflowStep step
                ? items.IndexOf(step) : -1;
            set => SelectedItem = ItemsSource is IList<Models.WorkflowStep> items &&
                value >= 0 && value < items.Count ? items[value] : null;
        }
        public void SelectAll()
        {
            SelectedItems.Clear();
            if (ItemsSource is IEnumerable<Models.WorkflowStep> items)
                foreach (var item in items) SelectedItems.Add(item);
            _selectedItem = SelectedItems.FirstOrDefault();
        }
        public void ScrollIntoView(object item) { }
    }
    public class StepEditorWindow : System.Windows.Window
    {
        public static Func<Models.WorkflowStep, bool?>? OnShow { get; set; }
        public Models.WorkflowStep Result { get; }
        public StepEditorWindow(Models.WorkflowStep step) => Result = step;
        public bool? ShowDialog() => OnShow?.Invoke(Result) ?? false;
    }
    public partial class VisualRecorderWindow
    {
        private readonly TestControl CaptureToggle = new();
        private readonly TestControl CaptureStatus = new();
        private readonly TestControl TargetStatus = new();
        private readonly TestControl RecordingBanner = new();
        private readonly TestControl RecordingDot = new();
        private readonly TestControl RecordedCountLabel = new();
        private readonly TestControl EmptyStepsHint = new();
        private readonly TestControl StepList = new();
        private readonly TestControl SaveStepsButton = new();
        private void InitializeComponent() => Closed += Window_Closed;
        public void TestLoad() => RaiseLoaded();
        public void TestSave() => Save_Click(this, new());
        public void TestCancel() => Cancel_Click(this, new());
        public void TestToggle() => ToggleCapture_Click(this, new());
        public void TestSelectFirst() => StepList.SelectedItem = RecordedSteps.FirstOrDefault();
        public void TestRemove() => Remove_Click(this, new());
        public void TestSelectAll() => SelectAll_Click(this, new());
        public int TestSelectedCount => StepList.SelectedItems.Count;
        public void TestTick() => _drainTimer.Fire();
        public void TestPauseWithoutDrain() => _recorder.Pause();
        public void TestClear() => Clear_Click(this, new());
        public void TestCaptureUrl() => CaptureUrl_Click(this, new());
        public void TestEditStep(string type, string target) => EditNew(new Models.WorkflowStep { Type = type, Target = target });
        public Task TestWaitForUpdatesAsync() => _recorder.SynchronizeAsync();
        public bool TestToggleEnabled => CaptureToggle.IsEnabled;
        public string? TestStatus => (CaptureStatus.Text ?? "") + " " + (TargetStatus.Text ?? "");
    }
}
