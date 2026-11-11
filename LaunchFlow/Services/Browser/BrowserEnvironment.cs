namespace LaunchFlow.Services;

/// <summary>
/// Summarizes which browsers LaunchFlow found on this PC for readiness UI and logs.
/// </summary>
public static class BrowserEnvironment
{
    public sealed record Report(
        bool Edge,
        bool Chrome,
        bool Brave,
        bool Opera,
        bool OperaGx,
        bool Vivaldi,
        bool MozillaFirefox,
        bool AutomationFirefox,
        string? EdgePath,
        string? ChromePath,
        string? BravePath,
        string? OperaPath,
        string? OperaGxPath,
        string? VivaldiPath,
        string? MozillaFirefoxPath,
        string Summary,
        string? AutomationFirefoxPath = null)
    {
        public bool Firefox => MozillaFirefox || AutomationFirefox;
        public bool AnyAutoBrowser => Edge || Chrome || Brave || Opera || OperaGx || Vivaldi || AutomationFirefox;
    }

    public static Report Probe()
    {
        var edgePath = BrowserInstallations.FindExecutable("Edge");
        var chromePath = BrowserInstallations.FindExecutable("Chrome");
        var bravePath = BrowserInstallations.FindExecutable("Brave");
        var operaPath = BrowserInstallations.FindExecutable("Opera");
        var operaGxPath = BrowserInstallations.FindExecutable("Opera GX");
        var vivaldiPath = BrowserInstallations.FindExecutable("Vivaldi");
        var mozillaPath = BrowserInstallations.ProbeMozillaFirefoxPath();
        var automationFirefoxPath = PlaywrightFirefoxSupport.InstalledBrowsersPath;
        var automationFirefox = automationFirefoxPath is not null;

        var parts = new List<string>();
        if (edgePath is not null) parts.Add("Edge");
        if (chromePath is not null) parts.Add("Chrome");
        if (bravePath is not null) parts.Add("Brave");
        if (operaPath is not null) parts.Add("Opera");
        if (operaGxPath is not null) parts.Add("Opera GX");
        if (vivaldiPath is not null) parts.Add("Vivaldi");
        if (automationFirefox) parts.Add("Firefox Auto (ready)");
        else if (mozillaPath is not null)
            parts.Add("regular Firefox (automatic sign-in needs LaunchFlow’s Firefox)");

        var summary = parts.Count == 0
            ? "No supported browser found yet. Install Edge, Chrome, Brave, Opera, Opera GX, or Vivaldi — or use Install Firefox below."
            : "Ready on this PC: " + string.Join(", ", parts) +
              ". LaunchFlow opens the browser you pick and signs in when you ask.";

        return new Report(
            edgePath is not null,
            chromePath is not null,
            bravePath is not null,
            operaPath is not null,
            operaGxPath is not null,
            vivaldiPath is not null,
            mozillaPath is not null,
            automationFirefox,
            edgePath,
            chromePath,
            bravePath,
            operaPath,
            operaGxPath,
            vivaldiPath,
            mozillaPath,
            summary,
            automationFirefoxPath);
    }
}
