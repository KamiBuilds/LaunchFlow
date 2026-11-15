using System.Windows;
using LaunchFlow.Services;
using MessageBox = System.Windows.MessageBox;

namespace LaunchFlow;

public partial class SetupWindow : Window
{
    public string ChosenPassword { get; private set; } = "";
    public bool StartWithWindows => StartupCheck.IsChecked == true;
    public bool RunAfterUnlock => UnlockCheck.IsChecked == true;
    public bool RunOnAppStart => LaunchCheck.IsChecked == true;
    public bool RunInTray => TrayCheck.IsChecked == true;

    public SetupWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            DarkWindowChrome.Apply(this);
            PasswordBox.Focus();
        };
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        var assessment = PasswordStrength.Evaluate(PasswordBox.Password);
        StrengthLabel.Text = $"Strength: {assessment.Label}";
        StrengthHint.Text = assessment.Hint;
        StrengthLabel.Foreground = assessment.Score switch
        {
            <= 1 => new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71)),
            2 => new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFB, 0xBF, 0x24)),
            _ => new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x6E, 0xE7, 0xD7))
        };
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Password;
        var confirm = ConfirmBox.Password;
        var assessment = PasswordStrength.Evaluate(password);
        if (!assessment.Acceptable)
        {
            MessageBox.Show(this, assessment.Hint,
                "Choose a stronger master password", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (password != confirm)
        {
            MessageBox.Show(this, "Passwords did not match.",
                "Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ChosenPassword = password;
        PasswordBox.Clear();
        ConfirmBox.Clear();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        PasswordBox.Clear();
        ConfirmBox.Clear();
        ChosenPassword = "";
        DialogResult = false;
    }
}
