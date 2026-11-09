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
        WebsiteTitle.Text = "Check sign-in for " + website;
        ReasonText.Text = reason + "\n\nConfirmation applies only to this open browser session. It never saves a password, disables MFA or approves another website.";
        Loaded += (_, _) => DarkWindowChrome.Apply(this);
    }

    private void Later_Click(object sender, RoutedEventArgs e) { Choice = Decision.Later; Close(); }
    private void Repair_Click(object sender, RoutedEventArgs e) { Choice = Decision.Repair; Close(); }
    private void Confirmed_Click(object sender, RoutedEventArgs e) { Choice = Decision.Confirmed; Close(); }
}