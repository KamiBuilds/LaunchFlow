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

/// <summary>KeePassXC database discovery and settings UI; credential prompts remain guarded.</summary>
public partial class MainWindow
{
    private void RefreshKeePassStatus()
    {
        var database = KeePassDbBox.Text.Trim();
        if (!KeePassXcService.IsCliAvailable())
        {
            KeePassStatusLabel.Text = "KeePassXC was not found. Install KeePassXC, then click Find my database.";
            return;
        }
        if (string.IsNullOrWhiteSpace(database))
        {
            KeePassStatusLabel.Text = "KeePassXC found. Choose a password database to continue.";
            return;
        }
        if (!File.Exists(database))
        {
            KeePassStatusLabel.Text = "The selected database is missing. Choose it again.";
            return;
        }
        KeePassStatusLabel.Text = $"KeePassXC found · {Path.GetFileName(database)} selected.";
    }

    private void SuggestKeePassDatabase()
    {
        if (!string.IsNullOrWhiteSpace(KeePassDbBox.Text)) return;
        var found = KeePassXcService.FindDatabases();
        if (found.Count == 1) KeePassDbBox.Text = found[0].Path;
    }

    private void FindKeePassDb_Click(object sender, RoutedEventArgs e)
    {
        var found = KeePassXcService.FindDatabases();
        if (found.Count == 0)
        {
            KeePassDbSuggestions.Visibility = Visibility.Collapsed;
            MessageBox.Show(this,
                "No KeePassXC database was found in the usual folders. Click Choose a file and select the .kdbx database you open in KeePassXC.",
                "Find KeePassXC database", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (found.Count == 1)
        {
            SetKeePassDatabase(found[0].Path);
            KeePassDbSuggestions.Visibility = Visibility.Collapsed;
            return;
        }

        KeePassDbSuggestions.ItemsSource = found;
        KeePassDbSuggestions.SelectedIndex = -1;
        KeePassDbSuggestions.Visibility = Visibility.Visible;
        KeePassStatusLabel.Text = $"Found {found.Count} databases. Choose the one you normally use.";
        KeePassDbSuggestions.Focus();
        KeePassDbSuggestions.IsDropDownOpen = true;
    }

    private void KeePassDbSuggestion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_configuration is null || KeePassDbSuggestions is null || KeePassDbBox is null) return;
        if (KeePassDbSuggestions.SelectedItem is KeePassXcService.DatabaseChoice selected)
        {
            SetKeePassDatabase(selected.Path);
            KeePassDbSuggestions.Visibility = Visibility.Collapsed;
        }
    }

    private void SetKeePassDatabase(string path)
    {
        KeePassDbBox.Text = path;
        _keepassMasterPassword = null;
        RefreshKeePassStatus();
    }

    private void UpdateKeePassEntryLabel()
    {
        var path = KeepassEntryBox.Text.Trim();
        SelectedKeePassEntryLabel.Text = string.IsNullOrWhiteSpace(path)
            ? "No KeePassXC login selected" : path;
    }

    private void KeePassEntryTextChanged(object sender, TextChangedEventArgs e)
    {
        if (SelectedKeePassEntryLabel is not null) UpdateKeePassEntryLabel();
    }


    private void BrowseKeePassDb_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select KeePassXC database",
            Filter = "KeePass database (*.kdbx)|*.kdbx|All files (*.*)|*.*"
        };
        if (!string.IsNullOrWhiteSpace(KeePassDbBox.Text))
        {
            try
            {
                var dir = Path.GetDirectoryName(KeePassDbBox.Text.Trim());
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    dialog.InitialDirectory = dir;
            }
            catch { /* ignore bad path */ }
        }
        if (dialog.ShowDialog(this) == true)
        {
            SetKeePassDatabase(dialog.FileName);
        }
    }

    private void BrowseKeePassKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select KeePass key file",
            Filter = "Key file (*.keyx;*.key)|*.keyx;*.key|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            KeePassKeyBox.Text = dialog.FileName;
            _keepassMasterPassword = null;
            RefreshKeePassStatus();
        }
    }

    /// <summary>
    /// Prompt once per vault session when a run needs KeePass and has no local vault password.
    /// </summary>
}
