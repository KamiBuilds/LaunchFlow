using System;

namespace System.Windows
{
    public static class FontWeights
    {
        public static readonly object Bold = "Bold";
        public static readonly object Normal = "Normal";
    }

    public enum MessageBoxButton { OK }
    public enum MessageBoxImage { Warning, Information }
    public enum MessageBoxResult { OK }
    public static class MessageBox
    {
        public static Action? BeforeShow;
        public static MessageBoxResult Show(object owner, string message, string title,
            MessageBoxButton buttons, MessageBoxImage image)
        {
            BeforeShow?.Invoke();
            return MessageBoxResult.OK;
        }
    }
}

namespace System.Windows.Media
{
    public readonly record struct Color(byte R, byte G, byte B)
    {
        public static Color FromRgb(byte r, byte g, byte b) => new(r, g, b);
    }
    public sealed class SolidColorBrush
    {
        public SolidColorBrush(Color color) => Color = color;
        public Color Color { get; }
    }
}

namespace System.Windows.Controls
{
    public sealed class ComboBoxItem
    {
        public object? Tag { get; set; }
    }
}

namespace System.Windows.Threading
{
    public sealed class DispatcherTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsEnabled { get; private set; }
        public event EventHandler? Tick;
        public void Start() => IsEnabled = true;
        public void Stop() => IsEnabled = false;
        public void Fire() { if (IsEnabled) Tick?.Invoke(this, EventArgs.Empty); }
    }
}

namespace LaunchFlow.Services
{
    public sealed class MonitorChoice
    {
        public int Index { get; init; }
        public string DeviceName { get; init; } = "";
    }
}

public sealed class FakeItems
{
    public int Refreshes { get; private set; }
    public void Refresh() => Refreshes++;
}

public sealed class FakeControl
{
    private string _text = "";
    private bool? _checked;
    private int _selectedIndex;
    private object? _selectedItem;
    public event EventHandler? TextChanged;
    public event EventHandler? Checked;
    public event EventHandler? Unchecked;
    public event EventHandler? SelectionChanged;
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            TextChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public bool? IsChecked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            if (value == true) Checked?.Invoke(this, EventArgs.Empty);
            else Unchecked?.Invoke(this, EventArgs.Empty);
        }
    }
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value) return;
            _selectedIndex = value;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public object? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (ReferenceEquals(_selectedItem, value)) return;
            _selectedItem = value;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public FakeItems Items { get; } = new();
    public object? Foreground { get; set; }
    public object? FontWeight { get; set; }
    public object? Content { get; set; }
    public object? ToolTip { get; set; }
    public string Password { get; set; } = "";
}
