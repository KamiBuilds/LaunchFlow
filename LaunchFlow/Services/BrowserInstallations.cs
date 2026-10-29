using System.IO;
using Microsoft.Win32;

namespace LaunchFlow.Services;

/// <summary>Detects installed Windows browser executables without launching them.</summary>
public static class BrowserInstallations
{
    public const string DefaultBrowser = "Default browser (open only)";
    private static readonly (string Name, string FileName, string RelativePath)[] Browsers =
    [
        ("Edge", "msedge.exe", @"Microsoft\Edge\Application\msedge.exe"),
        ("Chrome", "chrome.exe", @"Google\Chrome\Application\chrome.exe"),
        ("Firefox", "firefox.exe", @"Mozilla Firefox\firefox.exe")
    ];

    public static IReadOnlyList<string> InstalledNames() =>
        Browsers.Where(b => FindExecutable(b.Name) is not null).Select(b => b.Name).ToArray();

    public static bool IsInstalled(string name) =>
        string.Equals(name, DefaultBrowser, StringComparison.Ordinal) ||
        FindExecutable(name) is not null;

    public static string? FindExecutable(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var browser = Browsers.FirstOrDefault(b =>
            string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(browser.FileName)) return null;

        // Windows App Paths is the source of truth for conventional installations;
        // search both registry views because Edge/Chrome/Firefox have 32-bit variants.
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

        // Portable/local installations do not always register an App Paths entry.
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        foreach (var root in roots.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var valid = VerifyCandidate(Path.Combine(root, browser.RelativePath), browser.FileName);
            if (valid is not null) return valid;
        }
        // Intentionally no "installed if browser appears in the dropdown" fallback.
        return null;
    }

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
