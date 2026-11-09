using System.IO;
using LaunchFlow.Services;

internal static class BrowserInstallationTests
{
    public static IEnumerable<(string Name, Action Body)> Cases()
    {
        yield return ("default-browser open-only is always selectable but arbitrary browsers are not", () =>
        {
            Check(BrowserInstallations.IsInstalled(BrowserInstallations.DefaultBrowser),
                "default-browser open-only must be offered");
            Check(!BrowserInstallations.IsInstalled("Unknown") &&
                  BrowserInstallations.FindExecutable("Unknown") is null,
                "unknown browser should never be selectable");
        });
        yield return ("detected browsers have existing matching Windows executables", () =>
        {
            var names = BrowserInstallations.InstalledNames();
            Check(names.Count == names.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                "duplicate browser choices");
            foreach (var name in names)
            {
                var file = BrowserInstallations.FindExecutable(name);
                Check(file is not null && File.Exists(file) &&
                    BrowserInstallations.IsInstalled(name),
                    "browser was claimed installed without a matching executable");
            }
            if (!OperatingSystem.IsWindows())
                Check(names.Count == 0 && BrowserInstallations.FindExecutable("Firefox") is null,
                    "non-Windows runner must not invent Windows browser installations");
        });
        yield return ("Firefox Auto requires Playwright automation Firefox, not stock mozilla.org", () =>
        {
            // Stock mozilla.org firefox.exe is never enough for Auto.
            var automation = PlaywrightFirefoxSupport.IsAutomationFirefoxInstalled();
            var names = BrowserInstallations.InstalledNames();
            if (automation)
            {
                Check(BrowserInstallations.IsInstalled("Firefox") &&
                      names.Contains("Firefox", StringComparer.OrdinalIgnoreCase),
                    "Playwright/bundled Firefox must make Firefox selectable for Auto");
            }
            else
            {
                Check(!BrowserInstallations.IsInstalled("Firefox") &&
                      !names.Contains("Firefox", StringComparer.OrdinalIgnoreCase),
                    "stock Mozilla Firefox alone must not offer Auto Firefox");
            }
        });
        yield return ("Edge and Chrome Auto use discovered installed executables when present", () =>
        {
            var report = BrowserEnvironment.Probe();
            if (report.Edge)
                Check(!string.IsNullOrWhiteSpace(report.EdgePath) &&
                      System.IO.File.Exists(report.EdgePath) &&
                      BrowserInstallations.IsInstalled("Edge"),
                    "found Edge must resolve to an existing msedge.exe");
            if (report.Chrome)
                Check(!string.IsNullOrWhiteSpace(report.ChromePath) &&
                      System.IO.File.Exists(report.ChromePath) &&
                      BrowserInstallations.IsInstalled("Chrome"),
                    "found Chrome must resolve to an existing chrome.exe");
            Check(report.Summary.Contains("Found on this PC", StringComparison.Ordinal) ||
                  report.Summary.Contains("No supported browser", StringComparison.Ordinal),
                "probe summary must describe discovery results");
        });
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
