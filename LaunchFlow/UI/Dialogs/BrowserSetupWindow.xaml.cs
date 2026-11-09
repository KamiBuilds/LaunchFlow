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
        ReadinessText.Text = report.Edge || report.Chrome
            ? report.Summary
            : "Neither Microsoft Edge nor Google Chrome was detected. Automation requires an approved browser.";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
    private void Edge_Click(object sender, RoutedEventArgs e) =>
        OpenOfficial("https://www.microsoft.com/edge/download");
    private void Chrome_Click(object sender, RoutedEventArgs e) =>
        OpenOfficial("https://www.google.com/chrome/");

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
