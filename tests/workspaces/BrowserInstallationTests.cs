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
                Check(string.Equals(name, "Firefox", StringComparison.OrdinalIgnoreCase)
                        ? PlaywrightFirefoxSupport.IsAutomationFirefoxInstalled() &&
                          BrowserInstallations.IsInstalled(name)
                        : file is not null && File.Exists(file) &&
                          BrowserInstallations.IsInstalled(name),
                    "browser was claimed installed without an available automation executable");
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
        yield return ("Firefox readiness selects the same browser cache used at launch", () =>
        {
            var temp = Path.Combine(Path.GetTempPath(), "launchflow-firefox-test-" + Guid.NewGuid().ToString("N"));
            var bundled = Path.Combine(temp, "bundled");
            var user = Path.Combine(temp, "user");
            var cache = Path.Combine(temp, "ms-playwright");
            try
            {
                Check(PlaywrightFirefoxSupport.ResolveLaunchBrowsersPath(bundled, user, cache) == user,
                    "no Firefox build must fall back to the consent-install directory");
                CreateFirefoxBuild(cache);
                Check(PlaywrightFirefoxSupport.ResolveLaunchBrowsersPath(bundled, user, cache) == cache,
                    "a default-cache-only Firefox must launch from its detected cache");
                Directory.CreateDirectory(user);
                Check(PlaywrightFirefoxSupport.ResolveLaunchBrowsersPath(bundled, user, cache) == cache,
                    "an empty user directory must not hide a valid default cache");
                CreateFirefoxBuild(user);
                Check(PlaywrightFirefoxSupport.ResolveLaunchBrowsersPath(bundled, user, cache) == user,
                    "a consent-installed Firefox must take priority over the default cache");
                CreateFirefoxBuild(bundled);
                Check(PlaywrightFirefoxSupport.ResolveLaunchBrowsersPath(bundled, user, cache) == bundled,
                    "the bundled Firefox must take priority over user and default caches");
            }
            finally
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
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

    private static void CreateFirefoxBuild(string root)
    {
        var bin = Path.Combine(root, "firefox-1234", "firefox");
        Directory.CreateDirectory(bin);
        File.WriteAllBytes(Path.Combine(bin, "firefox.exe"), Array.Empty<byte>());
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
