using System.Diagnostics;
using System.Windows;
using LaunchFlow.Services;
using MessageBox = System.Windows.MessageBox;

namespace LaunchFlow;

public partial class BrowserSetupWindow : Window
{
    public BrowserSetupWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var report = BrowserEnvironment.Probe();
        ReadinessText.Text = report.AnyAutoBrowser
            ? report.Summary
            : "No browser ready for automatic sign-in yet. Install one below, or install Firefox.";
        DetailText.Text =
            Line("Edge", report.Edge) +
            Line("Chrome", report.Chrome) +
            Line("Brave", report.Brave) +
            Line("Opera", report.Opera) +
            Line("Opera GX", report.OperaGx) +
            Line("Vivaldi", report.Vivaldi) +
            Line("Regular Firefox", report.MozillaFirefox) +
            "Firefox Auto: " + (report.AutomationFirefox ? "ready" : "not installed");
        FirefoxStatusText.Text = report.AutomationFirefox
            ? "Firefox is ready for automatic sign-in."
            : "Firefox for automatic sign-in is not installed yet. Edge, Chrome, Brave, Opera, and Vivaldi still work when found.";
        InstallFirefoxAutomationButton.IsEnabled = !report.AutomationFirefox;
    }

    private static string Line(string name, bool found) =>
        name + ": " + (found ? "found" : "not found") + "\n";

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
    private void Edge_Click(object sender, RoutedEventArgs e) =>
        OpenOfficial("https://www.microsoft.com/edge/download");
    private void Chrome_Click(object sender, RoutedEventArgs e) =>
        OpenOfficial("https://www.google.com/chrome/");
    private void Brave_Click(object sender, RoutedEventArgs e) =>
        OpenOfficial("https://brave.com/download/");
    private void Opera_Click(object sender, RoutedEventArgs e) =>
        OpenOfficial("https://www.opera.com/download");

    private void InstallFirefoxAutomation_Click(object sender, RoutedEventArgs e)
    {
        if (PlaywrightFirefoxSupport.IsAutomationFirefoxInstalled())
        {
            Refresh();
            return;
        }

        if (MessageBox.Show(this,
                "Download LaunchFlow’s Firefox for automatic sign-in (~100+ MB)?\n\n" +
                "This is a special Firefox LaunchFlow uses. Regular Firefox from Mozilla cannot do automatic sign-in.\n\n" +
                "If Edge, Chrome, Brave, Opera, or Vivaldi is already installed, you can use those instead.",
                "Install Firefox",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        InstallFirefoxAutomationButton.IsEnabled = false;
        FirefoxStatusText.Text = "Downloading Firefox…";
        try
        {
            var code = PlaywrightFirefoxSupport.InstallAutomationFirefox();
            if (code != 0 || !PlaywrightFirefoxSupport.IsAutomationFirefoxInstalled())
            {
                MessageBox.Show(this,
                    "Firefox download did not finish. Check your internet and try again — or use Edge, Chrome, Brave, Opera, or Vivaldi.",
                    "Browsers", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(this,
                    "Firefox is ready. Choose Firefox for a website and try automatic sign-in again.",
                    "Browsers", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception)
        {
            MessageBox.Show(this,
                "Firefox install did not work. Use Edge, Chrome, Brave, Opera, or Vivaldi instead.",
                "Browsers", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Refresh();
        }
    }

    private void OpenOfficial(string url)
    {
        if (MessageBox.Show(this,
            "Open this website in your usual browser? LaunchFlow will not install anything by itself.",
            "Open website", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            MessageBox.Show(this, "Windows could not open that website. Ask your IT person for help.",
                "Browsers", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
