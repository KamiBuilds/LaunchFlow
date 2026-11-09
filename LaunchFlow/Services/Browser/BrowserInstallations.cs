using System.IO;
using Microsoft.Win32;

namespace LaunchFlow.Services;

/// <summary>
/// Finds browsers already installed on Windows. Edge/Chrome Auto uses those
/// binaries; Firefox Auto uses LaunchFlow's shipped/consent Playwright Firefox
/// (stock mozilla.org Firefox cannot be automated).
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
        ("Firefox", "firefox.exe",
        [
            @"Mozilla Firefox\firefox.exe",
            @"Firefox Developer Edition\firefox.exe",
            @"Mozilla Firefox\private_browsing.exe"
        ])
    ];

    public static IReadOnlyList<string> InstalledNames()
    {
        var names = new List<string>();
        foreach (var browser in Browsers)
        {
            if (string.Equals(browser.Name, "Firefox", StringComparison.OrdinalIgnoreCase))
            {
                // Auto Firefox requires the Playwright build (installer-shipped or
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

    /// <summary>Absolute path to an installed Edge/Chrome/Mozilla Firefox.exe, if any.</summary>
    public static string? FindExecutable(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var browser = Browsers.FirstOrDefault(b =>
            string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(browser.FileName)) return null;

        // Prefer App Paths (both registry views — 32-bit installs on 64-bit Windows).
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + browser.FileName);
                var path = key?.GetValue(null) as string;
                var valid = VerifyCandidate(path, browser.FileName);
                if (valid is not null) return valid;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            { /* registry access may be restricted on enterprise Windows */ }
        }

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        foreach (var root in roots.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            foreach (var relative in browser.RelativePaths)
            {
                // private_browsing.exe is not a launch target for Auto.
                if (relative.EndsWith("private_browsing.exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                var valid = VerifyCandidate(Path.Combine(root, relative), browser.FileName);
                if (valid is not null) return valid;
            }
        }
        return null;
    }

    public static bool IsMozillaFirefoxInstalled() => FindExecutable("Firefox") is not null;

    private static string? VerifyCandidate(string? path, string expectedExecutable)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (!Path.GetFileName(path).Equals(expectedExecutable, StringComparison.OrdinalIgnoreCase))
                return null;
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        { return null; }
    }
}
