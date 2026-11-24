namespace System.Windows
{
    public class RoutedEventArgs : EventArgs { }
    public delegate void RoutedEventHandler(object sender, RoutedEventArgs e);
    public class Window
    {
        public static Func<Window, bool?>? OnShow { get; set; }
        public bool? DialogResult { get; set; }
        public event EventHandler? Closed;
        public event RoutedEventHandler Loaded { add { } remove { } }
        public bool? ShowDialog() => OnShow?.Invoke(this) ?? DialogResult;
        public void Close() => Closed?.Invoke(this, EventArgs.Empty);
    }
    public enum MessageBoxButton { OK, YesNo, YesNoCancel }
    public enum MessageBoxImage { Warning, Information, Question, Error }
    public enum MessageBoxResult { OK, Yes, No, Cancel }
    public static class MessageBox
    {
        public static List<string> Text { get; } = [];
        public static void Show(Window owner, string text, string title, MessageBoxButton button, MessageBoxImage image) => Text.Add(text);
        public static void Show(Window owner, string text) => Text.Add(text);
        public static MessageBoxResult Show(string text, string title, MessageBoxButton button, MessageBoxImage image) { Text.Add(text); return MessageBoxResult.Cancel; }
    }
}
namespace System.Windows.Markup { public class XamlParseException : Exception { } }
namespace LaunchFlow.Services { public static class DarkWindowChrome { public static void Apply(System.Windows.Window _) { } } }
namespace LaunchFlow
{
    public partial class App { }
    public sealed class TextInput { public string Text { get; set; } = ""; public void Focus() { } public void Clear() => Text = ""; }
    public sealed class CheckInput { public bool? IsChecked { get; set; } }
    public partial class VaultFreshStartWindow : System.Windows.Window
    {
        public TextInput ConfirmationText { get; } = new();
        public CheckInput PreserveCheck { get; } = new();
        private void InitializeComponent() { }
    }
    public partial class VaultRecoveryWindow : System.Windows.Window
    {
        private void InitializeComponent() { }
    }
}
