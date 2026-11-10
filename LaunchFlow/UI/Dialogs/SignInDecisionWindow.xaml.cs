using System.Windows;
using LaunchFlow.Services;

namespace LaunchFlow;

public partial class SignInDecisionWindow : Window
{
    public enum Decision { Later, Repair, Confirmed }
    public Decision Choice { get; private set; } = Decision.Later;

    public SignInDecisionWindow(string website, string reason)
    {
        InitializeComponent();
        WebsiteTitle.Text = website;
        ReasonText.Text = reason + "\n\nSaying “I’m signed in” only applies to this open browser window. It doesn’t save a password or change your settings.";
        Loaded += (_, _) => DarkWindowChrome.Apply(this);
    }

    private void Later_Click(object sender, RoutedEventArgs e) { Choice = Decision.Later; Close(); }
    private void Repair_Click(object sender, RoutedEventArgs e) { Choice = Decision.Repair; Close(); }
    private void Confirmed_Click(object sender, RoutedEventArgs e) { Choice = Decision.Confirmed; Close(); }
}