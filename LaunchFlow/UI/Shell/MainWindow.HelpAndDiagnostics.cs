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

    /// <summary>
    /// Guide-only navigation: open the correct tab/area so Show target can outline
    /// a control. Never clicks, fills, saves, or runs automation.
    /// </summary>
    internal bool TryPrepareGuideTarget(string windowType, string targetName, out string message)
    {
        message = "";
        if (_exiting || _vaultLockInProgress || _windowsLocked || !IsLoaded)
        {
            message = "Finish vault unlock or wait for LaunchFlow to finish cleaning up, then try again.";
            return false;
        }

        if (windowType == "NewWorkflowWindow")
        {
            if (_newWorkflowWindow is { IsLoaded: true, IsVisible: true })
            {
                _newWorkflowWindow.Activate();
                message = "Website setup is already open.";
                return true;
            }
            message = "Click + Add website first so the setup window opens. The guide will not open it for you while edits may be pending.";
            return false;
        }

        if (windowType == "VisualRecorderWindow")
        {
            if (_activeRecorder is { IsLoaded: true, IsVisible: true })
            {
                _activeRecorder.Activate();
                message = "The recorder window is open.";
                return true;
            }
            message = "Start Record clicks on a website first. The guide never starts recording by itself.";
            return false;
        }

        if (windowType != "MainWindow")
        {
            message = "Open the named window yourself, then use Show target again.";
            return false;
        }

        // Dirty editors can trigger save/validation on tab change — refuse rather
        // than surprise the user with a modal. Recovery text covers the rest.
        if (_editorSession.IsDirty || HasPendingEditorDialog)
        {
            message = "Save or discard pending website edits first (or close the open setup/editor window), then try again.";
            return false;
        }

        try
        {
            if (targetName is "WorkflowsNav" or "AddWebsiteToolbarButton" or "WorkflowList"
                or "RunSelectedButton" or "SaveStatusLabel" or "SaveChangesButton"
                or "RepairButton" or "TeachButton" or "RetryVaultLockButton"
                or "CreateFirstWorkflowButton")
            {
                MainTabs.SelectedIndex = 0;
                WorkflowsNav.IsChecked = true;
                Activate();
                message = "Opened Websites.";
                return true;
            }

            if (targetName == "SettingsNav")
            {
                MainTabs.SelectedIndex = 1;
                SettingsNav.IsChecked = true;
                Activate();
                message = "Opened Settings.";
                return true;
            }

            if (targetName is "PasswordsSettingsTab" or "PasswordWebsiteCombo" or "LoginModeCombo"
                or "UsernameBox" or "PasswordInput" or "SaveCredentialsButton" or "ClearCredentialButton"
                or "KeepassEntryBox" or "AutoLoginCheck")
            {
                OpenSettingsCategory(1);
                SettingsNav.IsChecked = true;
                Activate();
                message = "Opened Settings → Passwords.";
                return true;
            }

            if (targetName == "StartupSettingsTab" ||
                targetName.Contains("Startup", StringComparison.Ordinal))
            {
                OpenSettingsCategory(2);
                SettingsNav.IsChecked = true;
                Activate();
                message = "Opened Settings → Startup.";
                return true;
            }

            Activate();
            message = "LaunchFlow is ready. Look for the outlined control.";
            return true;
        }
        catch (Exception)
        {
            message = "Could not open that area safely. Use Can't find it? for a manual path.";
            return false;
        }
    }

    internal void SetGuideCoach(string? text)
    {
        if (GuideCoachBanner is null || GuideCoachText is null) return;
        if (string.IsNullOrWhiteSpace(text))
        {
            GuideCoachBanner.Visibility = Visibility.Collapsed;
            GuideCoachText.Text = "";
            return;
        }
        GuideCoachText.Text = text;
        GuideCoachBanner.Visibility = Visibility.Visible;
    }

}
