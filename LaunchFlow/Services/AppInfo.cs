using System.Reflection;

namespace LaunchFlow.Services;

public static class AppInfo
{
    public static string ProductName => "LaunchFlow";

    /// <summary>
    /// Optional GitHub Releases API URL. Set in Settings, or assign before check
    /// when you publish releases (e.g. https://api.github.com/repos/you/LaunchFlow/releases/latest).
    /// </summary>
    public static string? DefaultUpdateFeedUrl { get; set; }

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
