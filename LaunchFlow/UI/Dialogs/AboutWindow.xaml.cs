using System.Reflection;
using System.Windows;
using LaunchFlow.Services;

namespace LaunchFlow;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => DarkWindowChrome.Apply(this);
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null
            ? "Version 0.7.19"
            : $"Version {version.Major}.{version.Minor}.{version.Build}";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
