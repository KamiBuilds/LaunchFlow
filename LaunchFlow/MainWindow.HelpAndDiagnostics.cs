using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Win32;
using Microsoft.Playwright;
using Forms = System.Windows.Forms;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace LaunchFlow;

/// <summary>About, browser setup and privacy-safe diagnostic export.</summary>
public partial class MainWindow
{
    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.Show();
    }

    private void BrowserSetup_Click(object sender, RoutedEventArgs e) =>
        new BrowserSetupWindow { Owner = this }.Show();

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Export a local support report with application version and redacted event codes? No websites, credentials or workflow details will be included.",
                "Export diagnostics", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save redacted diagnostics",
            Filter = "Text report (*.txt)|*.txt",
            DefaultExt = ".txt",
            FileName = "LaunchFlow-Diagnostics.txt"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            DiagnosticReportService.Export(dialog.FileName);
            MessageBox.Show(this, "Redacted diagnostic report saved.", "Diagnostics");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not export diagnostics: " + ex.GetType().Name,
                "Diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

}
