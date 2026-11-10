using System.Windows;
using LaunchFlow.Services;

namespace LaunchFlow;

public enum CloseChoice
{
    Cancel,
    Background,
    Exit
}

public partial class CloseChoiceWindow : Window
{
    public CloseChoice Choice { get; private set; } = CloseChoice.Cancel;

    public CloseChoiceWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => DarkWindowChrome.Apply(this);
    }

    private void Background_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Background;
        DialogResult = true;
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Exit;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Cancel;
        DialogResult = false;
    }
}
