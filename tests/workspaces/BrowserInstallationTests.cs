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
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
