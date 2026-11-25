namespace System.Windows
{
    public class RoutedEventArgs : EventArgs { }
    public delegate void RoutedEventHandler(object sender, RoutedEventArgs args);
    public class Window
    {
        public string Title { get; set; } = "";
        public bool? DialogResult { get; set; }
        public event EventHandler? Closed;
        protected virtual void OnClosed(EventArgs e) => Closed?.Invoke(this, e);
        public void CloseForTest() => OnClosed(EventArgs.Empty);
        public event RoutedEventHandler Loaded { add { } remove { } }
    }
    public enum MessageBoxButton { OK }
    public enum MessageBoxImage { Warning }
    public static class MessageBox
    {
        public static string? LastMessage { get; private set; }
        public static string? LastTitle { get; private set; }
        public static int Count { get; private set; }
        public static void Reset() { LastMessage = null; LastTitle = null; Count = 0; }
        public static void Show(Window owner, string text, string title, MessageBoxButton button, MessageBoxImage image)
        {
            LastMessage = text;
            LastTitle = title;
            Count++;
        }
    }
}
namespace System.Windows.Markup { public class XamlParseException : Exception { } }
namespace System.Windows.Media
{
    public readonly record struct Color(byte Red, byte Green, byte Blue)
    {
        public static Color FromRgb(byte red, byte green, byte blue) => new(red, green, blue);
    }
    public sealed class SolidColorBrush(Color color) { public Color Color { get; } = color; }
}
namespace LaunchFlow.Services
{
    public static class DarkWindowChrome
    {
        public static void Apply(System.Windows.Window window) { }
    }
}
namespace LaunchFlow
{
    public sealed class PasswordInput
    {
        public string Password { get; set; } = "";
        public void Clear() => Password = "";
        public int FocusCount { get; private set; }
        public void Focus() => FocusCount++;
    }
    public sealed class CheckInput { public bool? IsChecked { get; set; } }
    public sealed class TextOutput
    {
        public string Text { get; set; } = "";
        public object? Foreground { get; set; }
    }
    public partial class SetupWindow
    {
        public PasswordInput PasswordBox { get; } = new();
        public PasswordInput ConfirmBox { get; } = new();
        public CheckInput StartupCheck { get; } = new();
        public CheckInput UnlockCheck { get; } = new();
        public CheckInput LaunchCheck { get; } = new();
        public CheckInput TrayCheck { get; } = new();
        public TextOutput StrengthLabel { get; } = new();
        public TextOutput StrengthHint { get; } = new();
        private void InitializeComponent() { }
        public void SubmitForTest() => Create_Click(this, new System.Windows.RoutedEventArgs());
        public void UpdateStrengthForTest() => PasswordBox_PasswordChanged(this, new System.Windows.RoutedEventArgs());
        public void CancelForTest() => Cancel_Click(this, new System.Windows.RoutedEventArgs());
    }
}

namespace LaunchFlow
{
    public partial class SecretPromptWindow
    {
        public TextOutput TitleText { get; } = new();
        public TextOutput MessageText { get; } = new();
        public PasswordInput SecretBox { get; } = new();
        private void InitializeComponent() { }
        public void SubmitForTest() => Ok_Click(this, new System.Windows.RoutedEventArgs());
        public void CancelForTest() => Cancel_Click(this, new System.Windows.RoutedEventArgs());
    }
}
