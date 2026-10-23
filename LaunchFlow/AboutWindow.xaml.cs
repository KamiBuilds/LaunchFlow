using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using LaunchFlow.Services;
using MessageBox = System.Windows.MessageBox;

namespace LaunchFlow;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => DarkWindowChrome.Apply(this);
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null
            ? "Version 0.7.9"
            : $"Version {version.Major}.{version.Minor}.{version.Build}";
        DataPathText.Text = VaultDatabase.Current.DatabasePath;
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(VaultBackupService.DataDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = VaultBackupService.DataDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot open folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
