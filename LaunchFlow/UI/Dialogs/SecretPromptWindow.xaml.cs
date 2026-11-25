using System.Windows;
using LaunchFlow.Services;

namespace LaunchFlow;

public partial class SecretPromptWindow : Window
{
    public string Secret { get; private set; } = "";

    // Only opt-in for NEW master/backup passwords. Existing vault unlock and
    // encrypted backup import must accept the original password unchanged.
    public bool RequireStrongPassword { get; init; }

    public SecretPromptWindow(string title, string message)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        Loaded += (_, _) =>
        {
            DarkWindowChrome.Apply(this);
            SecretBox.Focus();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (RequireStrongPassword)
        {
            var assessment = PasswordStrength.Evaluate(SecretBox.Password);
            if (!assessment.Acceptable)
            {
                MessageBox.Show(this, PasswordStrength.RejectionNotice(assessment),
                    "Choose a stronger password", MessageBoxButton.OK, MessageBoxImage.Warning);
                SecretBox.Focus(); // Stay on the same prompt; do not accept or lose the entry.
                return;
            }
        }
        Secret = SecretBox.Password;
        SecretBox.Clear();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SecretBox.Clear();
        Secret = "";
        DialogResult = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        SecretBox.Clear();
        base.OnClosed(e);
    }
}
