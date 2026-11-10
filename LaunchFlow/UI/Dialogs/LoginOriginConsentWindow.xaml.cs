using System.Windows;
using LaunchFlow.Services;

namespace LaunchFlow;

public partial class LoginOriginConsentWindow : Window
{
    public LoginOriginConsentWindow(string websiteName, string passwordOrigin)
    {
        InitializeComponent();
        WebsiteTitle.Text = "Confirm sign-in for " + websiteName;
        OriginText.Text = passwordOrigin;
        Loaded += (_, _) =>
        {
            DarkWindowChrome.Apply(this);
            Activate();
            RejectButton.Focus();
        };
    }

    private void Approve_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Reject_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
