namespace LaunchFlow.Services;

/// <summary>
/// Summarizes which browsers LaunchFlow found on this PC for readiness UI and logs.
/// </summary>
public static class BrowserEnvironment
{
    public sealed record Report(
        bool Edge,
        bool Chrome,
        bool MozillaFirefox,
        bool AutomationFirefox,
        string? EdgePath,
        string? ChromePath,
        string? MozillaFirefoxPath,
        string Summary)
    {
        public bool Firefox => MozillaFirefox || AutomationFirefox;
        public bool AnyAutoBrowser => Edge || Chrome || AutomationFirefox;
    }

    public static Report Probe()
    {
        var edgePath = BrowserInstallations.FindExecutable("Edge");
        var chromePath = BrowserInstallations.FindExecutable("Chrome");
        var mozillaPath = BrowserInstallations.FindExecutable("Firefox");
        var automationFirefox = PlaywrightFirefoxSupport.IsAutomationFirefoxInstalled();

        var parts = new List<string>();
        if (edgePath is not null) parts.Add("Edge (installed)");
        if (chromePath is not null) parts.Add("Chrome (installed)");
        if (automationFirefox) parts.Add("Firefox Auto (automation ready)");
        else if (mozillaPath is not null)
            parts.Add("Mozilla Firefox found (Auto needs LaunchFlow Firefox automation)");

        var summary = parts.Count == 0
            ? "No supported browser detected. Install Microsoft Edge or Google Chrome, or use Browser readiness."
            : "Found on this PC: " + string.Join(", ", parts) +
              ". Auto uses your installed Edge/Chrome; Firefox Auto uses the Setup-shipped automation build.";

        return new Report(
            edgePath is not null,
            chromePath is not null,
            mozillaPath is not null,
            automationFirefox,
            edgePath,
            chromePath,
            mozillaPath,
            summary);
    }
}
