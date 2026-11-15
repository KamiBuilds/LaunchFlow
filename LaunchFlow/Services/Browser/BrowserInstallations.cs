using System.IO;
using Microsoft.Win32;

namespace LaunchFlow.Services;

/// <summary>
/// Finds browsers already installed on Windows. Chromium-family browsers
/// (Edge, Chrome, Brave, Opera, Opera GX, Vivaldi) Auto-launch via their
/// installed .exe. Firefox Auto uses LaunchFlow's shipped/consent automation
/// Firefox (stock mozilla.org Firefox cannot be automated).
/// </summary>
public static class BrowserInstallations
{
    public const string DefaultBrowser = "Default browser (open only)";
    private static readonly (string Name, string FileName, string[] RelativePaths)[] Browsers =
    [
        ("Edge", "msedge.exe",
        [
            @"Microsoft\Edge\Application\msedge.exe",
            @"Microsoft\Edge Beta\Application\msedge.exe",
            @"Microsoft\Edge Dev\Application\msedge.exe"
        ]),
        ("Chrome", "chrome.exe",
        [
            @"Google\Chrome\Application\chrome.exe",
            @"Google\Chrome Beta\Application\chrome.exe",
            @"Google\Chrome Dev\Application\chrome.exe"
        ]),
        ("Brave", "brave.exe",
        [
            @"BraveSoftware\Brave-Browser\Application\brave.exe",
            @"BraveSoftware\Brave-Browser-Beta\Application\brave.exe",
            @"BraveSoftware\Brave-Browser-Dev\Application\brave.exe"
        ]),
        ("Opera", "opera.exe",
        [
            @"Programs\Opera\opera.exe",
            @"Programs\Opera\launcher.exe"
        ]),
        ("Opera GX", "opera.exe",
        [
            @"Programs\Opera GX\opera.exe",
            @"Programs\Opera GX\launcher.exe"
        ]),
        ("Vivaldi", "vivaldi.exe",
        [
            @"Vivaldi\Application\vivaldi.exe"
        ]),
        ("Firefox", "firefox.exe",
        [
            @"Mozilla Firefox\firefox.exe",
            @"Firefox Developer Edition\firefox.exe",
            @"Mozilla Firefox\private_browsing.exe"
        ])
    ];

    public static IReadOnlyList<string> KnownChromiumNames { get; } =
        Browsers.Where(b => !string.Equals(b.Name, "Firefox", StringComparison.OrdinalIgnoreCase))
            .Select(b => b.Name).ToArray();

    public static IReadOnlyList<string> InstalledNames()
    {
        var names = new List<string>();
        foreach (var browser in Browsers)
        {
            if (string.Equals(browser.Name, "Firefox", StringComparison.OrdinalIgnoreCase))
            {
                // Auto Firefox requires the automation build (installer-shipped or
                // consent-installed). Stock Mozilla is detected for UI status only.
                if (PlaywrightFirefoxSupport.IsAutomationFirefoxInstalled())
                    names.Add(browser.Name);
                continue;
            }
            if (FindExecutable(browser.Name) is not null)
                names.Add(browser.Name);
        }
        return names;
    }

    public static bool IsInstalled(string name) =>
        string.Equals(name, DefaultBrowser, StringComparison.Ordinal) ||
        (name.Contains("Firefox", StringComparison.OrdinalIgnoreCase)
            ? PlaywrightFirefoxSupport.IsAutomationFirefoxInstalled()
            : FindExecutable(name) is not null);

    /// <summary>Absolute path to an installed browser .exe for the named choice, if any.</summary>
    public static string? FindExecutable(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var browser = MatchBrowser(name);
        if (browser is null) return null;
        var entry = browser.Value;

        // Prefer App Paths (both registry views — 32-bit installs on 64-bit Windows).
        // Skip for Opera GX: App Paths "opera.exe" often points at regular Opera.
        if (!string.Equals(entry.Name, "Opera GX", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var key = root.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + entry.FileName);
                    var path = key?.GetValue(null) as string;
                    var valid = VerifyCandidate(path, entry.FileName, entry.Name);
                    if (valid is not null) return valid;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                { /* registry access may be restricted on enterprise Windows */ }
            }
        }

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        foreach (var root in roots.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            foreach (var relative in entry.RelativePaths)
            {
                if (relative.EndsWith("private_browsing.exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                var valid = VerifyCandidate(Path.Combine(root, relative), entry.FileName, entry.Name);
                if (valid is not null) return valid;
            }
        }
        return null;
    }

    /// <summary>Stock Mozilla Firefox from mozilla.org (status only — not used for Auto).</summary>
    public static bool IsMozillaFirefoxInstalled() => ProbeMozillaFirefoxPath() is not null;

    public static string? ProbeMozillaFirefoxPath()
    {
        // FindExecutable("Firefox") resolves the stock firefox.exe via App Paths / folders.
        // Auto never launches it; Browser readiness only shows it as informational.
        return FindExecutable("Firefox");
    }

    private static (string Name, string FileName, string[] RelativePaths)? MatchBrowser(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var exact = Browsers.FirstOrDefault(b =>
            string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(exact.FileName)) return exact;
        // Saved choices like "Google Chrome" / "Microsoft Edge" / "Opera GX Gaming".
        foreach (var browser in Browsers.OrderByDescending(b => b.Name.Length))
        {
            if (name.Contains(browser.Name, StringComparison.OrdinalIgnoreCase))
                return browser;
        }
        return null;
    }

    private static string? VerifyCandidate(string? path, string expectedExecutable, string browserName)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            var fileName = Path.GetFileName(path);
            // Opera GX and Opera both use opera.exe / launcher.exe.
            if (!fileName.Equals(expectedExecutable, StringComparison.OrdinalIgnoreCase) &&
                !(string.Equals(expectedExecutable, "opera.exe", StringComparison.OrdinalIgnoreCase) &&
                  fileName.Equals("launcher.exe", StringComparison.OrdinalIgnoreCase)))
                return null;
            if (!File.Exists(path)) return null;
            path = Path.GetFullPath(path);
            // Keep Opera vs Opera GX distinct by install folder.
            if (string.Equals(browserName, "Opera GX", StringComparison.OrdinalIgnoreCase))
            {
                if (path.IndexOf("Opera GX", StringComparison.OrdinalIgnoreCase) < 0)
                    return null;
            }
            else if (string.Equals(browserName, "Opera", StringComparison.OrdinalIgnoreCase))
            {
                if (path.IndexOf("Opera GX", StringComparison.OrdinalIgnoreCase) >= 0)
                    return null;
            }
            return path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        { return null; }
    }
}
