using System.IO;

namespace LaunchFlow.Services;

/// <summary>
/// Playwright can only automate its own patched Firefox build — not the
/// Mozilla Firefox from mozilla.org. Stock firefox.exe may open a window, but
/// page.Goto / fill never run.
/// Preferred layout: ship <c>playwright-browsers</c> next to LaunchFlow.exe
/// (installer). Fallback: consent install under LocalAppData.
/// </summary>
public static class PlaywrightFirefoxSupport
{
    public const string BundledFolderName = "playwright-browsers";

    public static string BundledBrowsersPath =>
        Path.Combine(AppContext.BaseDirectory, BundledFolderName);

    public static string UserBrowsersPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaunchFlow", BundledFolderName);

    /// <summary>
    /// Must run before any Playwright API so launches find the shipped Firefox.
    /// </summary>
    public static void ConfigureProcessBrowsersPath()
    {
        var preferred = HasFirefoxBuild(BundledBrowsersPath)
            ? BundledBrowsersPath
            : UserBrowsersPath;
        try { Directory.CreateDirectory(preferred); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", preferred);
    }

    public static bool IsAutomationFirefoxInstalled() =>
        HasFirefoxBuild(BundledBrowsersPath) || HasFirefoxBuild(UserBrowsersPath) ||
        HasFirefoxBuild(DefaultMsPlaywrightCachePath());

    public static bool HasFirefoxBuild(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return false;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "firefox-*"))
            {
                if (Directory.EnumerateFiles(dir, "firefox.exe", SearchOption.AllDirectories).Any())
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return false;
    }

    /// <summary>
    /// Downloads only Playwright's Firefox into the user browsers path. Caller
    /// must obtain explicit consent first.
    /// </summary>
    public static int InstallAutomationFirefox()
    {
        ConfigureProcessBrowsersPath();
        var target = UserBrowsersPath;
        Directory.CreateDirectory(target);
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", target);
        var previousSkip = Environment.GetEnvironmentVariable("PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD");
        try
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD", null);
            var program = Type.GetType("Microsoft.Playwright.Program, Microsoft.Playwright", throwOnError: true)!;
            var main = program.GetMethod("Main", [typeof(string[])])
                ?? throw new InvalidOperationException("Playwright install entry point is missing.");
            return (int)main.Invoke(null, [new[] { "install", "firefox" }])!;
        }
        finally
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD", previousSkip ?? "1");
            ConfigureProcessBrowsersPath();
        }
    }

    private static string DefaultMsPlaywrightCachePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ms-playwright");
}
