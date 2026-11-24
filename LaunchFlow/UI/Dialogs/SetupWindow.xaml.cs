using System.Windows;
using LaunchFlow.Services;
using MessageBox = System.Windows.MessageBox;

namespace LaunchFlow;

public partial class SetupWindow : Window
{
    private bool _submitted;
    public string ChosenPassword { get; private set; } = "";

    // Consume once; immutable strings cannot be zeroed, but the dialog must not retain the handoff.
    internal string TakeChosenPassword()
    {
        var password = ChosenPassword;
        ChosenPassword = "";
        return password;
    }
    public bool StartWithWindows => StartupCheck.IsChecked == true;
    public bool RunAfterUnlock => UnlockCheck.IsChecked == true;
    public bool RunOnAppStart => LaunchCheck.IsChecked == true;
    public bool RunInTray => TrayCheck.IsChecked == true;

    public SetupWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            PasswordBox.Clear();
            ConfirmBox.Clear();
            if (DialogResult != true) ChosenPassword = "";
        };
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
        StrengthLabel.Foreground = new System.Windows.Media.SolidColorBrush(
            assessment.Acceptable
                ? System.Windows.Media.Color.FromRgb(0x6E, 0xE7, 0xD7)
                : (assessment.Score == 2
                    ? System.Windows.Media.Color.FromRgb(0xFB, 0xBF, 0x24)
                    : System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71)));
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_submitted) return;
        var password = PasswordBox.Password;
        var confirm = ConfirmBox.Password;
        StartupDiagnostics.Record(StartupDiagnostics.Stage.SetupAssessment);
        var assessment = PasswordStrength.Evaluate(password);
        StartupDiagnostics.RecordAdmission(StartupDiagnostics.Admission.UiAcceptable, assessment.Acceptable);
        StartupDiagnostics.RecordAdmission(StartupDiagnostics.Admission.UiMeetsMinimum, PasswordStrength.MeetsMinimum(password));
        StartupDiagnostics.RecordAdmission(StartupDiagnostics.Admission.ConfirmationEqual,
            string.Equals(password, confirm, StringComparison.Ordinal));
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

        _submitted = true;
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
