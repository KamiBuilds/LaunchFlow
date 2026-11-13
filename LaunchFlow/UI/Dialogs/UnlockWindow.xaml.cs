using System.Windows;
using LaunchFlow.Services;
using MessageBox = System.Windows.MessageBox;

namespace LaunchFlow;

public partial class UnlockWindow : Window
{
    public string? Password { get; private set; }
    public bool ResetRequested { get; private set; }
    /// <summary>True when Password came from Windows Hello recovery, not typed entry.</summary>
    public bool UsedWindowsHello { get; private set; }

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
        // Collect only. Freshness is published by callers after real verification.
        Password = PasswordBox.Password;
        UsedWindowsHello = false;
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
            UsedWindowsHello = true;
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
            "Forgot your master password?\n\n" +
            "Preferred: cancel and import a .launchflow backup from Settings → Import / Export " +
            "(or restore a file from %LOCALAPPDATA%\\LaunchFlow\\recovery if you have one).\n\n" +
            "Resetting permanently deletes the saved websites and passwords on this computer. " +
            "You can import a previously exported LaunchFlow file after setup.\n\nContinue with reset?",
            "Reset LaunchFlow", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var again = MessageBox.Show(this,
            "This cannot be undone. Reset LaunchFlow and remove its saved browser profiles?",
            "Confirm reset", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (again != MessageBoxResult.Yes) return;

        try
        {
            VaultResetService.ResetLocalVault(wipeProfiles: true);
            ResetRequested = true;
            MessageBox.Show(this,
                "The local vault was reset. LaunchFlow will open first-time setup next.",
                "Vault reset", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = false;
        }
        catch (AggregateException)
        {
            MessageBox.Show(this,
                "Reset is incomplete. Some browser profiles or vault files may still remain. " +
                "Close applications using those files and retry; no complete wipe is claimed.",
                "Reset incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception)
        {
            MessageBox.Show(this, "Reset could not finish. Existing files may remain.",
                "Reset failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        PasswordBox.Clear();
        Password = null;
        UsedWindowsHello = false;
        DialogResult = false;
    }
}
