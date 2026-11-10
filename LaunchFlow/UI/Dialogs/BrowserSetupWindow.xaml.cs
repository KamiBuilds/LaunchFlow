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
            : "No Auto-ready browser detected. Install Edge or Chrome, or install Firefox automation below.";
        DetailText.Text =
            "Edge: " + Describe(report.Edge, report.EdgePath) + "\n" +
            "Chrome: " + Describe(report.Chrome, report.ChromePath) + "\n" +
            "Mozilla Firefox: " + Describe(report.MozillaFirefox, report.MozillaFirefoxPath) + "\n" +
            "Firefox Auto: " + (report.AutomationFirefox
                ? "ready — " + report.AutomationFirefoxPath
                : "not installed");
        FirefoxStatusText.Text = report.AutomationFirefox
            ? "Firefox automation is ready for Auto sign-in."
            : "Firefox automation is not installed yet. Edge/Chrome Auto still work when those browsers are found.";
        InstallFirefoxAutomationButton.IsEnabled = !report.AutomationFirefox;
    }

    private static string Describe(bool found, string? path) =>
        found ? "found" + (string.IsNullOrWhiteSpace(path) ? "" : " — " + path) : "not found";

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
    private void Edge_Click(object sender, RoutedEventArgs e) =>
        OpenOfficial("https://www.microsoft.com/edge/download");
    private void Chrome_Click(object sender, RoutedEventArgs e) =>
        OpenOfficial("https://www.google.com/chrome/");

    private void InstallFirefoxAutomation_Click(object sender, RoutedEventArgs e)
    {
        if (PlaywrightFirefoxSupport.IsAutomationFirefoxInstalled())
        {
            Refresh();
            return;
        }

        if (MessageBox.Show(this,
                "Download LaunchFlow's Firefox automation browser (~100+ MB) into your user profile?\n\n" +
                "This is not the Mozilla Firefox from mozilla.org. Regular Firefox cannot be automated.\n\n" +
                "Prefer Edge or Chrome if they are already installed on this PC.",
                "Install Firefox automation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        InstallFirefoxAutomationButton.IsEnabled = false;
        FirefoxStatusText.Text = "Downloading Firefox automation…";
        try
        {
            var code = PlaywrightFirefoxSupport.InstallAutomationFirefox();
            if (code != 0 || !PlaywrightFirefoxSupport.IsAutomationFirefoxInstalled())
            {
                MessageBox.Show(this,
                    "Firefox automation download did not finish (code " + code + "). " +
                    "Check your network, then try again — or use Edge/Chrome.",
                    "Browser setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(this,
                    "Firefox automation is ready. Choose Firefox on a website and run Auto sign-in again.",
                    "Browser setup", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Firefox automation install failed: " + ex.GetType().Name + ". Use Edge or Chrome instead.",
                "Browser setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Refresh();
        }
    }

    private void OpenOfficial(string url)
    {
        if (MessageBox.Show(this,
            "Open this publisher's website in your default browser? LaunchFlow will not install anything automatically.",
            "Open browser website", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            MessageBox.Show(this, "Your system could not open the browser website. Contact your IT administrator.",
                "Browser setup", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
