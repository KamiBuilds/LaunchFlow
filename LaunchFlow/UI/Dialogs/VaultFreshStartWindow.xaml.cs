using System.Windows;
using LaunchFlow.Services;
using MessageBox = System.Windows.MessageBox;

namespace LaunchFlow;

public partial class VaultFreshStartWindow : Window
{
    private bool _submitted;

    public VaultFreshStartWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { DarkWindowChrome.Apply(this); ConfirmationText.Focus(); };
        Closed += (_, _) => { ConfirmationText.Clear(); PreserveCheck.IsChecked = false; };
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_submitted) return;
        if (PreserveCheck.IsChecked != true ||
            !string.Equals(ConfirmationText.Text, "START NEW VAULT", StringComparison.Ordinal))
        {
            MessageBox.Show(this, "Acknowledge preservation and type START NEW VAULT exactly to continue.",
                "Confirmation required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _submitted = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_submitted) return;
        DialogResult = false;
    }
}
