using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace LaunchFlow.Services;

/// <summary>
/// Populates browser selectors from browsers found on this PC. Missing saved
/// selections are displayed explicitly and preserved, never mapped to Edge.
/// </summary>
public static class BrowserChoices
{
    public static string SelectedName(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    public static void Refresh(ComboBox combo, string? preferred = null)
    {
        var selected = preferred ?? SelectedName(combo);
        var available = BrowserInstallations.InstalledNames();
        combo.Items.Clear();
        foreach (var browser in available)
        {
            var tip = string.Equals(browser, "Firefox", StringComparison.OrdinalIgnoreCase)
                ? "Uses LaunchFlow’s special Firefox for automatic sign-in (included with Setup)."
                : "Found on this PC — LaunchFlow will open it for you.";
            combo.Items.Add(new ComboBoxItem { Content = browser, Tag = browser, ToolTip = tip });
        }

        // Keep a saved choice even after its browser is uninstalled, rather
        // than silently rewriting the encrypted workflow's browser setting.
        if (!string.IsNullOrWhiteSpace(selected) &&
            selected != BrowserInstallations.DefaultBrowser &&
            !available.Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            var hint = selected.Contains("Firefox", StringComparison.OrdinalIgnoreCase)
                ? "LaunchFlow’s Firefox is missing. Reinstall LaunchFlow or open Browser readiness."
                : "This browser is not installed right now. Install it, or pick another browser.";
            combo.Items.Add(new ComboBoxItem
            {
                Content = selected + " (not available)",
                Tag = selected, IsEnabled = false,
                ToolTip = hint
            });
        }

        combo.Items.Add(new ComboBoxItem
        {
            Content = BrowserInstallations.DefaultBrowser,
            Tag = BrowserInstallations.DefaultBrowser,
            ToolTip = "Only opens the website in your usual Windows browser — no automatic sign-in."
        });

        var match = combo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, selected, StringComparison.OrdinalIgnoreCase));
        combo.SelectedItem = match ?? combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i =>
            !string.Equals(i.Tag as string, BrowserInstallations.DefaultBrowser, StringComparison.Ordinal))
            ?? combo.Items[combo.Items.Count - 1];
    }
}
