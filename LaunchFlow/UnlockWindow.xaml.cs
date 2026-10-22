using System.Windows;
using LaunchFlow.Services;
using MessageBox = System.Windows.MessageBox;

namespace LaunchFlow;

public partial class UnlockWindow : Window
{
    public string? Password { get; private set; }
    public bool ResetRequested { get; private set; }

    public UnlockWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            DarkWindowChrome.Apply(this);
            PasswordBox.Focus();
            var helloOk = WindowsHelloService.IsEnabled && await WindowsHelloService.IsSupportedAsync();
            HelloButton.Visibility = helloOk ? Visibility.Visible : Visibility.Collapsed;
            if (helloOk)
                StatusText.Text = "";
        };
    }

    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PasswordBox.Password))
        {
            StatusText.Text = "Enter your master password.";
            return;
        }
        Password = PasswordBox.Password;
        if (WindowsHelloService.IsEnabled)
            WindowsHelloService.MarkPasswordVerified();
        PasswordBox.Clear();
        DialogResult = true;
    }

    private async void Hello_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Waiting for Windows Hello…";
        HelloButton.IsEnabled = false;
        try
        {
            if (WindowsHelloService.NeedsPasswordReauth(30))
            {
                StatusText.Text = "Periodic security check: enter your master password once, then Windows Hello works again.";
                PasswordBox.Focus();
                return;
            }

            var secret = await WindowsHelloService.TryUnlockPasswordAsync(30);
            if (string.IsNullOrEmpty(secret))
            {
                StatusText.Text = "Windows Hello was cancelled or failed. Enter your master password instead.";
                return;
            }
            Password = secret;
            DialogResult = true;
        }
        finally
        {
            HelloButton.IsEnabled = true;
        }
    }

    private void Forgot_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "LaunchFlow cannot recover your master password.\n\n" +
            "Reset deletes the encrypted vault on this PC so you can create a new one.\n\n" +
            "Before resetting: if you still have an encrypted copy in the backups folder, " +
            "cancel here, unlock somehow, and use Settings → Security → Restore from backup… " +
            "Or after a fresh setup, import a portable .launchflow export.\n\n" +
            "Reset the local vault now?",
            "Reset local vault",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var again = MessageBox.Show(this,
            "Also delete sealed browser profiles and local encrypted backups?\n\n" +
            "Yes = wipe vault + profiles + backups\nNo = wipe vault only (keep backups folder)",
            "What to delete",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (again == MessageBoxResult.Cancel) return;

        try
        {
            VaultResetService.ResetLocalVault(
                wipeProfiles: true,
                wipeBackups: again == MessageBoxResult.Yes);
            ResetRequested = true;
            MessageBox.Show(this,
                "The local vault was reset. LaunchFlow will open first-time setup next.",
                "Vault reset", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Reset failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        PasswordBox.Clear();
        Password = null;
        DialogResult = false;
    }
}
