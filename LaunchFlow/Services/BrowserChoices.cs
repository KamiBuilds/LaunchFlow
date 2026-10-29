using System.Windows.Controls;

namespace LaunchFlow.Services;

/// <summary>
/// Populates browser selectors from actual Windows installations. Missing saved
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
            combo.Items.Add(new ComboBoxItem { Content = browser, Tag = browser,
                ToolTip = "Detected on this PC" });

        // Keep a saved choice even after its browser is uninstalled, rather
        // than silently rewriting the encrypted workflow's browser setting.
        if (!string.IsNullOrWhiteSpace(selected) &&
            selected != BrowserInstallations.DefaultBrowser &&
            !available.Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = selected + " (not installed)",
                Tag = selected, IsEnabled = false,
                ToolTip = "Install this browser or select an available browser to run this website."
            });
        }

        combo.Items.Add(new ComboBoxItem
        {
            Content = BrowserInstallations.DefaultBrowser,
            Tag = BrowserInstallations.DefaultBrowser,
            ToolTip = "Opens the website using Windows' default browser without automatic login."
        });

        var match = combo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, selected, StringComparison.OrdinalIgnoreCase));
        combo.SelectedItem = match ?? combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i =>
            !string.Equals(i.Tag as string, BrowserInstallations.DefaultBrowser, StringComparison.Ordinal))
            ?? combo.Items[combo.Items.Count - 1];
    }
}
