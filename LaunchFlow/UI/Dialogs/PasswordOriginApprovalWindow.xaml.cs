using System.Windows;
using LaunchFlow.Services;

namespace LaunchFlow;

public partial class PasswordOriginApprovalWindow : Window
{
    public PasswordOriginApprovalWindow(string websiteName, string origin)
    {
        InitializeComponent();
        WebsiteText.Text = websiteName;
        OriginText.Text = origin;
        Loaded += (_, _) =>
        {
            DarkWindowChrome.Apply(this);
            // The password page may be the active foreground browser process.
            // This transient topmost modal must be visible above that window.
            Activate();
        };
    }

    private void Approve_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
