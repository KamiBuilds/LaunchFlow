using System.Reflection;

namespace LaunchFlow.Services;

public static class AppInfo
{
    public static string ProductName => "LaunchFlow";

    /// <summary>
    /// Default GitHub Releases API URL for updates. Can be overridden in Settings
    /// or programmatically.
    /// </summary>
    public static string? DefaultUpdateFeedUrl { get; set; } = "https://api.github.com/repos/KamiBuilds/LaunchFlow/releases/latest";

    public static string VersionString
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "0.7.8" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static string SetupFileName => $"LaunchFlow-Setup-v{VersionString}-x64.exe";
}
