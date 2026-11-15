using System.Windows;
using LaunchFlow.Services;

namespace LaunchFlow;

public partial class PasswordOriginApprovalWindow : Window
{
    public PasswordOriginApprovalWindow(string websiteName, string origin,
        bool passwordDestination = true)
    {
        InitializeComponent();
        WebsiteText.Text = websiteName;
        OriginText.Text = origin;
        if (!passwordDestination)
        {
            Title = "Confirm username website";
            ConsentTitle.Text = "Approve this username website?";
            ConsentDescription.Text = "LaunchFlow reached another website for the username step:";
            ConsentWarning.Text = "Only approve this sign-in address if you recognize it. This will NOT authorize password filling there.";
            ApproveButton.Content = "Allow username on this site";
        }
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
