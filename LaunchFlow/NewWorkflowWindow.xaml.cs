using System.Windows;
using System.Windows.Controls;
using LaunchFlow.Models;
using LaunchFlow.Services;
using MessageBox = System.Windows.MessageBox;

namespace LaunchFlow;

public partial class NewWorkflowWindow : Window
{
    public WebsiteWorkflow? CreatedWorkflow { get; private set; }
    public bool WasCreated { get; private set; }
    public string NewUsername { get; private set; } = "";
    public string NewPassword { get; private set; } = "";

    public NewWorkflowWindow()
    {
        InitializeComponent();
        MonitorCombo.ItemsSource = MonitorService.GetMonitors();
        MonitorCombo.SelectedIndex = 0;
        Loaded += (_, _) =>
        {
            DarkWindowChrome.Apply(this);
            NameBox.Focus();
        };
        Closed += (_, _) =>
        {
            if (!WasCreated) ClearPendingSecrets();
        };
    }

    public void ClearPendingSecrets()
    {
        NewPassword = "";
        NewUsername = "";
        UsernameBox.Clear();
        PasswordBox.Clear();
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Unnamed workflow" : NameBox.Text.Trim();
        string url;
        try
        {
            url = BuildWebsiteUrl();
            AutomationEngine.ValidateUrl(url);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            MessageBox.Show(this, ex.Message, "Check website address", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var browser = (BrowserCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Edge";
        var monitor = (MonitorCombo.SelectedItem as MonitorChoice)?.Index ?? 0;
        var autoLogin = AutoLoginCheck.IsChecked == true;
        if (autoLogin && url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            (!string.IsNullOrEmpty(UsernameBox.Password) || !string.IsNullOrEmpty(PasswordBox.Password)))
        {
            var answer = MessageBox.Show(this,
                "HTTP connections are not encrypted. Anyone with access to the network may be able to see login details sent to this website. Continue anyway?",
                "Unencrypted connection", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }

        CreatedWorkflow = new WebsiteWorkflow
        {
            Name = name,
            StartUrl = url,
            Browser = browser,
            MonitorIndex = monitor,
            AutoLogin = autoLogin,
            LoginMode = autoLogin ? "Auto" : "None",
            Enabled = true,
            KeepassEntryPath = string.IsNullOrWhiteSpace(KeepassPathBox.Text)
                ? null
                : KeepassPathBox.Text.Trim()
        };

        NewUsername = UsernameBox.Password.Trim();
        NewPassword = PasswordBox.Password;
        WasCreated = true;
        Close();
    }

    // Normalize websites and local hosts into a complete HTTP(S) address.
    private string BuildWebsiteUrl()
    {
        var address = UrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("Enter a website address or IP, for example 192.168.1.50.");
        if (address.Any(char.IsWhiteSpace))
            throw new ArgumentException("The website address cannot contain spaces.");

        var hasScheme = address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        address.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!hasScheme && address.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("Use an HTTP or HTTPS address.");

        if (!hasScheme)
        {
            if (!Uri.TryCreate("http://" + address, UriKind.Absolute, out var probe))
                throw new ArgumentException("Enter a valid website address, IP or port.");
            var protocol = ProtocolCombo.SelectedIndex switch
            {
                1 => "https",
                2 => "http",
                _ => IsLocalAddress(probe.Host) ? "http" : "https"
            };
            address = protocol + "://" + address;
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.HostNameType == UriHostNameType.Unknown)
            throw new ArgumentException("Enter a valid HTTP or HTTPS website address.");

        if (uri.UserInfo.Length > 0)
            throw new ArgumentException("Do not put passwords in website addresses. Use the encrypted login fields instead.");

        var portText = PortBox.Text.Trim();
        if (portText.Length > 0)
        {
            if (!ushort.TryParse(portText, out var port) || port == 0)
                throw new ArgumentException("The port must be a number between 1 and 65535.");
            uri = new UriBuilder(uri) { Port = port }.Uri;
        }

        return uri.AbsoluteUri;
    }

    private static bool IsLocalAddress(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) ||
        !host.Contains('.') ||
        System.Net.IPAddress.TryParse(host, out _);

    private void PasteUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                MessageBox.Show(this, "Copy a website address from your browser first.",
                    "No address copied", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            UrlBox.Text = System.Windows.Clipboard.GetText().Trim();
            if (UrlBox.Text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                ProtocolCombo.SelectedIndex = 2;
            else if (UrlBox.Text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                ProtocolCombo.SelectedIndex = 1;
            UrlBox.Focus();
            UrlBox.CaretIndex = UrlBox.Text.Length;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not paste from the clipboard: " + ex.Message,
                "Paste address", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        WasCreated = false;
        ClearPendingSecrets();
        CreatedWorkflow = null;
        Close();
    }
}
