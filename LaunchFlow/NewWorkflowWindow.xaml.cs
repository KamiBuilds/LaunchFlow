using System.Windows;
using System.Windows.Controls;
using LaunchFlow.Models;
using LaunchFlow.Services;
using MessageBox = System.Windows.MessageBox;

namespace LaunchFlow;

public partial class NewWorkflowWindow : Window
{
    public WebsiteWorkflow? CreatedWorkflow { get; private set; }
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
        var url = UrlBox.Text.Trim();
        try
        {
            AutomationEngine.ValidateUrl(url);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid URL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var browser = (BrowserCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Edge";
        var monitor = (MonitorCombo.SelectedItem as MonitorChoice)?.Index ?? 0;
        var autoLogin = AutoLoginCheck.IsChecked == true;

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
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ClearPendingSecrets();
        CreatedWorkflow = null;
        DialogResult = false;
    }
}
