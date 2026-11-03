using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LaunchFlow.Services;

namespace LaunchFlow;

/// <summary>
/// Modeless, non-automating first-use help. A confirmed checkbox represents a
/// user's own observation, not proof of a completed save, credential, or run.
/// </summary>
public partial class TutorialWindow : Window
{
    // Source-verified English labels for the v0.7.46 UI. These routes deliberately
    // do not read live configuration, stored credentials or PasswordBox content.
    private sealed record GuideStep(
        string Id, string Heading, string Instruction, string Location,
        string Section, string ControlLabel, string Expected, string Recovery,
        string WindowType = "", string TargetName = "");

    private static readonly IReadOnlyDictionary<string, GuideStep[]> Routes =
        new Dictionary<string, GuideStep[]>(StringComparer.Ordinal)
        {
            ["basic"] =
            [
                new("BASIC-01", "Open Websites",
                    "Select Websites at the top of LaunchFlow.",
                    "LaunchFlow", "Top navigation", "Websites",
                    "The saved website list or 'No websites yet' appears.",
                    "If Settings is selected, click Websites in the top navigation. Do not close your guide.",
                    "MainWindow", "WorkflowsNav"),
                new("BASIC-02", "Open website setup",
                    "Click + Add website.",
                    "LaunchFlow", "Websites · top toolbar", "+ Add website",
                    "The 'Add a website - LaunchFlow' window opens, with 'Add your website' at the top.",
                    "When the list is empty, + Add your first website opens this same flow. If opening is blocked, unlock the vault or resolve unsaved edits before retrying.",
                    "MainWindow", "AddWebsiteToolbarButton"),
                new("BASIC-03", "Name this website",
                    "Replace the Website name with Demo website.",
                    "Add a website - LaunchFlow", "1. Which website?", "Website name",
                    "Website name now shows 'Demo website'. This is just an example; use your own name.",
                    "The field starts with 'My website'. Click inside that box and replace its contents.",
                    "NewWorkflowWindow", "NameBox"),
                new("BASIC-04", "Enter its address",
                    "Enter https://example.com in Website address (URL).",
                    "Add a website - LaunchFlow", "1. Which website?", "Website address (URL)",
                    "The address box shows https://example.com. This is an open-only example, not a login test account.",
                    "Include https:// for this example. Paste URL is an alternative; do not change advanced connection and port settings.",
                    "NewWorkflowWindow", "UrlBox"),
                new("BASIC-05", "Choose a browser",
                    "Open Browser and select an installed supported browser.",
                    "Add a website - LaunchFlow", "2. Where should it open?", "Browser",
                    "Browser shows the browser you chose.",
                    "Available browsers depend on this PC. Default browser can open a page but is not equivalent to a supported automation browser. Browser readiness can help if nothing compatible is installed.",
                    "NewWorkflowWindow", "BrowserCombo"),
                new("BASIC-06", "Choose a screen",
                    "Choose your desired display in Screen (monitor).",
                    "Add a website - LaunchFlow", "2. Where should it open?", "Screen (monitor)",
                    "Screen (monitor) shows your selected display.",
                    "A single-monitor PC does not need Screen 2. Monitor numbers vary by device; you can change the choice later.",
                    "NewWorkflowWindow", "MonitorCombo"),
                new("BASIC-07", "Keep login optional",
                    "Leave Yes, automatically sign in to this website unchecked.",
                    "Add a website - LaunchFlow", "3. Automatic login (optional)", "Yes, automatically sign in to this website",
                    "The checkbox remains unchecked and no website password is required for this example.",
                    "For a first test, leave login off. If the checkbox was enabled, uncheck it. Website credentials belong to an optional later guide.",
                    "NewWorkflowWindow", "AutoLoginCheck"),
                new("BASIC-08", "Create the website",
                    "Click Create website.",
                    "Add a website - LaunchFlow", "Bottom of website setup", "Create website",
                    "The setup window closes and the new website appears in Websites.",
                    "A validation warning or 'could not be saved' message means creation did not complete. Correct the entry and retry; do not delete your vault.",
                    "NewWorkflowWindow", "CreateWebsiteButton"),
                new("BASIC-09", "Select the new website",
                    "Select Demo website in your website list.",
                    "LaunchFlow", "Websites · YOUR WEBSITES", "Saved website workflows",
                    "The editor shows the selected website's name, address, browser and screen.",
                    "If no website is listed, creation may have failed. Check the previous step and its save result.",
                    "MainWindow", "WorkflowList"),
                new("BASIC-10", "Test only that website",
                    "Click Test this site.",
                    "LaunchFlow", "Websites · selected website editor", "▶ Test this site",
                    "LaunchFlow attempts to open only the selected website on the selected screen.",
                    "Test this site saves settings first. If saving, browser readiness or validation fails, resolve that issue before another attempt; Run all is not the same action.",
                    "MainWindow", "RunSelectedButton"),
                new("BASIC-11", "Check the browser result",
                    "Check the browser window that LaunchFlow opened.",
                    "Browser window", "Selected browser and display", "https://example.com",
                    "The intended address is shown on the intended monitor.",
                    "If no window opened, check the selected browser and screen and any error or sign-in notice. Avoid repeated launches while investigating."),
                new("BASIC-12", "Check the save result",
                    "Read the save status in LaunchFlow.",
                    "LaunchFlow", "Websites · top toolbar", "All changes saved",
                    "The status reads 'All changes saved' rather than 'Unsaved changes' or 'Save failed'.",
                    "At compact widths this caption can be hidden. Check the Save changes button and its tooltip; never treat a failed save as complete.",
                    "MainWindow", "SaveStatusLabel"),
                new("BASIC-13", "Save pending changes if needed",
                    "If edits are still pending, click Save changes.",
                    "LaunchFlow", "Websites · top toolbar", "Save changes",
                    "The application acknowledges a successful durable save, or shows a failure to correct.",
                    "Only press this if you have changes to keep. If saving fails or the vault is locked, leave edits pending and retry after resolving the cause.",
                    "MainWindow", "SaveChangesButton"),
            ],
            ["passwords"] =
            [
                new("PASS-01", "Open Settings",
                    "Select Settings in the top navigation.",
                    "LaunchFlow", "Top navigation", "Settings",
                    "Settings categories appear.",
                    "This route is optional. Finish testing a website without saved login first.",
                    "MainWindow", "SettingsNav"),
                new("PASS-02", "Open website passwords",
                    "Select Passwords in Settings.",
                    "LaunchFlow", "Settings · category list", "Passwords",
                    "The Website passwords editor appears, or requests a master-password unlock.",
                    "A vault master password is separate from the website password and Windows password. Never send it to support.",
                    "MainWindow", "PasswordsSettingsTab"),
                new("PASS-03", "Choose the credential target",
                    "Choose the intended site in Website account to update.",
                    "LaunchFlow", "Settings → Passwords", "Website account to update",
                    "The chosen website appears in the password account dropdown.",
                    "Check the selected account carefully, even if you selected another website in Websites. Unsaved password edits may require confirmation before switching.",
                    "MainWindow", "PasswordWebsiteCombo"),
                new("PASS-04", "Choose one sign-in mode",
                    "Choose your preferred option under How should LaunchFlow sign in?",
                    "LaunchFlow", "Settings → Passwords", "How should LaunchFlow sign in?",
                    "One mode is selected: saved password, no login or browser SSO.",
                    "SSO is for browser-led sign-in and can require manual MFA. Do not enable saved-password mode for HTTP or an untrusted origin.",
                    "MainWindow", "LoginModeCombo"),
                new("PASS-05", "Enter an account name",
                    "If updating it, enter a username in New username or email (leave blank to keep current).",
                    "LaunchFlow", "Settings → Passwords", "New username or email (leave blank to keep current)",
                    "Only the newly entered value is visible; a previously saved value is not prefilled.",
                    "Blank means keep the existing username. Never record or copy real credentials into this guide.",
                    "MainWindow", "UsernameBox"),
                new("PASS-06", "Enter a website password",
                    "If updating it, enter the website password in New password (leave blank to keep current).",
                    "LaunchFlow", "Settings → Passwords", "New password (leave blank to keep current)",
                    "The password stays masked in the application field.",
                    "Blank means keep a saved password, if one exists. LaunchFlow's vault password is not the password for this website.",
                    "MainWindow", "PasswordInput"),
                new("PASS-07", "Save to the chosen website",
                    "Click Save for selected website.",
                    "LaunchFlow", "Settings → Passwords", "Save for selected website",
                    "LaunchFlow either confirms encrypted credential saving or displays a failure.",
                    "Confirm the website picker and unlock state first. Saving here does not change your password at the website itself.",
                    "MainWindow", "SaveCredentialsButton"),
                new("PASS-08", "Read the credential status",
                    "Check the credential status for your chosen website.",
                    "LaunchFlow", "Settings → Passwords", "Encrypted credentials saved",
                    "The status confirms that the selected website's credentials were saved.",
                    "If the status still says 'No password saved' or an error appears, do not assume storage succeeded. A blocked IdP/MFA step needs manual attention.",
                    "MainWindow", "CredentialStatus"),
            ],
            ["recording"] =
            [
                new("RECORD-01", "Choose the website",
                    "Select the intended website from Websites.",
                    "LaunchFlow", "Websites · YOUR WEBSITES", "Saved website workflows",
                    "The correct site's details are visible.",
                    "Choose an owned, low-risk test page. Do not record real password or MFA entry.",
                    "MainWindow", "WorkflowList"),
                new("RECORD-02", "Start the recorder",
                    "Click Record clicks.",
                    "LaunchFlow", "Websites · Recorded actions (optional)", "● Record clicks",
                    "The 'Record actions - LaunchFlow' window opens.",
                    "This is a separate recorder window. It must attach to the page opened by LaunchFlow.",
                    "MainWindow", "TeachButton"),
                new("RECORD-03", "Check recorder readiness",
                    "Read the capture and target status.",
                    "Record actions - LaunchFlow", "Recorder · status", "CaptureStatus / TargetStatus",
                    "The recorder identifies the intended browser tab instead of staying on Connecting.",
                    "Wait for readiness before clicking. If it cannot attach, stop and investigate rather than recording another personal browser tab.",
                    "VisualRecorderWindow", "CaptureStatus"),
                new("RECORD-04", "Make one harmless click",
                    "Click one non-sensitive button in the original LaunchFlow browser tab.",
                    "Browser window", "Original recording tab", "A safe test button",
                    "A corresponding action appears in Recorded actions.",
                    "Do not enter passwords, account numbers, email addresses, OTPs or private text. Unsupported popups and frames may need manual setup."),
                new("RECORD-05", "Review the actions",
                    "Review the Recorded actions list before saving.",
                    "Record actions - LaunchFlow", "Recorder · recorded list", "Recorded actions",
                    "Only intended navigation and clicks are listed.",
                    "Remove accidental actions using the recorder controls. Secret-detection cannot be guaranteed for every custom web widget.",
                    "VisualRecorderWindow", "StepList"),
                new("RECORD-06", "Save reviewed actions",
                    "Click Save actions.",
                    "Record actions - LaunchFlow", "Recorder · bottom actions", "Save actions",
                    "The actions are offered to the selected website's workflow.",
                    "If the vault locks or the website was replaced, LaunchFlow must refuse stale recording ownership. Cancel does not mean Save.",
                    "VisualRecorderWindow", "SaveStepsButton"),
            ],
            ["startup"] =
            [
                new("STARTUP-01", "Open Settings",
                    "Select Settings in the top navigation.",
                    "LaunchFlow", "Top navigation", "Settings",
                    "Settings categories appear.",
                    "This optional route does not enable automation by itself.",
                    "MainWindow", "SettingsNav"),
                new("STARTUP-02", "Open Startup",
                    "Select Startup in Settings.",
                    "LaunchFlow", "Settings · category list", "Startup",
                    "Background and trigger options are visible.",
                    "These settings can be restricted by company policy. Do not enable them on a managed PC without approval.",
                    "MainWindow", "StartupSettingsTab"),
                new("STARTUP-03", "Review Windows startup",
                    "Review Start hidden with Windows (enables Ctrl+M).",
                    "LaunchFlow", "Settings → Startup", "Start hidden with Windows (enables Ctrl+M)",
                    "The checkbox reflects the current preference.",
                    "Turning this on requests startup registration; a checked setting alone does not prove Windows allowed it.",
                    "MainWindow", "StartupCheck"),
                new("STARTUP-04", "Review unlock automation",
                    "Review Run enabled websites after Windows unlock.",
                    "LaunchFlow", "Settings → Startup", "Run enabled websites after Windows unlock",
                    "The checkbox reflects whether an unlock-triggered run is requested.",
                    "Vault lock and Windows lock are different. Credentials may require unlocking before automation resumes.",
                    "MainWindow", "UnlockCheck"),
                new("STARTUP-05", "Review background mode",
                    "Review Allow LaunchFlow to run in the background (system tray).",
                    "LaunchFlow", "Settings → Startup", "Allow LaunchFlow to run in the background (system tray)",
                    "The background preference is visible.",
                    "Minimize, close-to-tray and Exit are different. Closing the window can show an Exit/Background choice.",
                    "MainWindow", "TrayCheck"),
            ],
            ["recovery"] =
            [
                new("HELP-01", "A browser is unavailable",
                    "Open Browser readiness and select Check again after installing an approved browser.",
                    "Browser readiness", "Browser readiness dialog", "Check again",
                    "The dialog updates its detected browser information.",
                    "The dialog does not silently install browsers. Use official downloads only when authorized by your device's software policy.",
                    "BrowserSetupWindow", "BrowserCheckAgainButton"),
                new("HELP-02", "The wrong website is selected",
                    "Select the correct website in YOUR WEBSITES.",
                    "LaunchFlow", "Websites · YOUR WEBSITES", "Saved website workflows",
                    "The selected website editor shows the intended name and address.",
                    "Do not select away from an unsaved editor without handling its warning. In Passwords, independently confirm Website account to update.",
                    "MainWindow", "WorkflowList"),
                new("HELP-03", "Automatic sign-in needs attention",
                    "Choose Fix sign-in on the selected website.",
                    "LaunchFlow", "Websites · Recorded actions (optional)", "Fix sign-in",
                    "LaunchFlow shows a repair or manual-attention path.",
                    "Use human sign-in for MFA/SSO. Do not disable HTTPS checks or guess untrusted login buttons.",
                    "MainWindow", "RepairButton"),
                new("HELP-04", "Saving failed",
                    "After fixing the error, click Save changes again.",
                    "LaunchFlow", "Websites · top toolbar", "Save changes",
                    "A successful durable save is acknowledged.",
                    "Keep pending edits intact. If the vault is locked, unlock it before retrying. A failed write is not a completed save.",
                    "MainWindow", "SaveChangesButton"),
                new("HELP-05", "Browser cleanup failed",
                    "After resolving the affected browser session, choose Retry vault lock.",
                    "LaunchFlow", "Websites · top toolbar", "Retry vault lock",
                    "The application retries a controlled vault lock and browser cleanup.",
                    "Do not force-delete profiles or assume browser sessions are closed if cleanup failed. This button appears only in the failure state.",
                    "MainWindow", "RetryVaultLockButton"),
            ]
        };

    private readonly HashSet<string> _confirmed = new(StringComparer.Ordinal);
    private string _route = "basic";
    private int _current;
    private bool _updating;
    private AdornerLayer? _highlightLayer;
    private TargetOutlineAdorner? _highlight;
    private FrameworkElement? _highlightTarget;
    private Window? _targetWindow;

    public TutorialWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => DarkWindowChrome.Apply(this);
        UpdateView();
    }

    private GuideStep CurrentStep => Routes[_route][_current];

    private void Route_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StepHeading is null || RouteCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string route || !Routes.ContainsKey(route)) return;

        _route = route;
        _current = 0;
        ClearHighlight();
        UpdateView();
    }

    private void UpdateView()
    {
        var step = CurrentStep;
        var pages = Routes[_route];
        ClearHighlight();
        _updating = true;
        try
        {
            ProgressLabel.Text = $"Step {_current + 1} of {pages.Length}";
            StepHeading.Text = step.Heading;
            StepDetails.Text = step.Instruction;
            ExpectedResult.Text = step.Expected;
            ExampleTitle.Text = step.Location;
            ExampleWebsite.Text = step.Section;
            ExampleAction.Text = step.ControlLabel;
            ExampleCaption.Text = "Look for this exact control or field label. The picture is a source-based locator, not a screenshot.";
            RecoveryText.Text = step.Recovery;
            RecoveryPanel.Visibility = Visibility.Collapsed;
            RecoveryButton.Content = "Can't find it?";
            TargetFeedback.Text = string.Empty;
            ConfirmStepBox.IsChecked = _confirmed.Contains(step.Id);
            ConfirmationStatus.Text = $"{pages.Count(p => _confirmed.Contains(p.Id))} of {pages.Length} results manually confirmed. " +
                "Next does not mark a task complete.";
            ShowTargetButton.IsEnabled = step.WindowType.Length > 0 && step.TargetName.Length > 0;
            BackButton.IsEnabled = _current > 0;
            NextButton.Content = _current == pages.Length - 1 ? "Finish" : "Next";
        }
        finally
        {
            _updating = false;
        }
    }

    private void ConfirmStep_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating || ConfirmStepBox is null) return;
        if (ConfirmStepBox.IsChecked == true) _confirmed.Add(CurrentStep.Id);
        else _confirmed.Remove(CurrentStep.Id);
        var pages = Routes[_route];
        ConfirmationStatus.Text = $"{pages.Count(p => _confirmed.Contains(p.Id))} of {pages.Length} results manually confirmed. " +
            "Next does not mark a task complete.";
    }

    private void Recovery_Click(object sender, RoutedEventArgs e)
    {
        var showing = RecoveryPanel.Visibility == Visibility.Visible;
        RecoveryPanel.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
        RecoveryButton.Content = showing ? "Can't find it?" : "Hide help";
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_current > 0) _current--;
        UpdateView();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_current == Routes[_route].Length - 1)
        {
            Close();
            return;
        }
        _current++;
        UpdateView();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowTarget_Click(object sender, RoutedEventArgs e)
    {
        ClearHighlight();
        var step = CurrentStep;
        if (step.WindowType.Length == 0 || step.TargetName.Length == 0) return;
        // Do not navigate to a different tab or select a different workflow:
        // those operations may trigger validation and unsaved-edit guards.
        var window = Application.Current.Windows.OfType<Window>()
            .FirstOrDefault(w => w.IsVisible && w.IsEnabled && w.GetType().Name == step.WindowType);
        var target = window?.FindName(step.TargetName) as FrameworkElement;
        if (window is null || target is null || !target.IsVisible || !target.IsEnabled)
        {
            TargetFeedback.Text = "Open the named window or section in LaunchFlow first. " +
                "No settings were changed. Use Can't find it? for a safe alternative.";
            return;
        }

        target.BringIntoView();
        var layer = AdornerLayer.GetAdornerLayer(target);
        if (layer is not null)
        {
            _highlight = new TargetOutlineAdorner(target);
            layer.Add(_highlight);
            _highlightLayer = layer;
            _highlightTarget = target;
            _targetWindow = window;
            target.Unloaded += HighlightTarget_Unloaded;
            window.Closed += HighlightWindow_Closed;
            TargetFeedback.Text = "The selected control is outlined in LaunchFlow. " +
                "This does not click it, enter text, save or run anything.";
        }
        else
        {
            TargetFeedback.Text = "The control is visible, but an outline is unavailable in this layout. " +
                "It was not clicked or modified.";
        }
        window.Activate(); // Only on the user's explicit Show target action.
    }

    private void HighlightTarget_Unloaded(object sender, RoutedEventArgs e) => ClearHighlight();
    private void HighlightWindow_Closed(object? sender, EventArgs e) => ClearHighlight();

    private void ClearHighlight()
    {
        if (_highlightTarget is not null)
            _highlightTarget.Unloaded -= HighlightTarget_Unloaded;
        if (_targetWindow is not null)
            _targetWindow.Closed -= HighlightWindow_Closed;
        if (_highlightLayer is not null && _highlight is not null)
            _highlightLayer.Remove(_highlight);
        _highlight = null;
        _highlightTarget = null;
        _targetWindow = null;
        _highlightLayer = null;
    }

    private void Guide_Closed(object sender, EventArgs e)
    {
        ClearHighlight();
        _confirmed.Clear();
    }

    private sealed class TargetOutlineAdorner : Adorner
    {
        public TargetOutlineAdorner(UIElement adornedElement) : base(adornedElement)
        {
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var rect = new Rect(AdornedElement.RenderSize);
            rect.Inflate(3, 3);
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(85, 216, 200)), 3);
            drawingContext.DrawRoundedRectangle(null, pen, rect, 5, 5);
        }
    }
}
