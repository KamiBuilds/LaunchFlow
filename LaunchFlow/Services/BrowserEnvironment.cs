using System.IO;
using Microsoft.Win32;

namespace LaunchFlow.Services;

/// <summary>
/// Detects browsers already installed on Windows so LaunchFlow can use
/// channel-based Playwright launches without downloading browser binaries.
/// </summary>
public static class BrowserEnvironment
{
    public sealed record Report(bool Edge, bool Chrome, bool Firefox, string Summary);

    public static Report Probe()
    {
        var edge = IsInstalled(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe")
                   || File.Exists(Path.Combine(
                       Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                       "Microsoft", "Edge", "Application", "msedge.exe"))
                   || File.Exists(Path.Combine(
                       Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                       "Microsoft", "Edge", "Application", "msedge.exe"));

        var chrome = IsInstalled(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe")
                     || File.Exists(Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Google", "Chrome", "Application", "chrome.exe"));

        var firefox = IsInstalled(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe")
                      || File.Exists(Path.Combine(
                          Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                          "Mozilla Firefox", "firefox.exe"));

        var parts = new List<string>();
        if (edge) parts.Add("Edge");
        if (chrome) parts.Add("Chrome");
        if (firefox) parts.Add("Firefox");

        var summary = parts.Count == 0
            ? "No supported browser detected. Install Microsoft Edge or Google Chrome for automation."
            : "Ready to use: " + string.Join(", ", parts) +
              ". LaunchFlow uses your installed browser — no extra browser download required for Edge/Chrome.";

        return new Report(edge, chrome, firefox, summary);
    }

    private static bool IsInstalled(string appPathsKey)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(appPathsKey)
                            ?? Registry.CurrentUser.OpenSubKey(appPathsKey);
            var path = key?.GetValue(null) as string;
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
