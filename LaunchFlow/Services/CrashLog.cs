using System.IO;

namespace LaunchFlow.Services;

/// <summary>
/// Local-only diagnostic log. Never uploads. Opt-in via configuration.
/// </summary>
public static class CrashLog
{
    public static string LogPath =>
        Path.Combine(VaultBackupService.DataDirectory, "diagnostics.log");

    public static void Write(string category, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(VaultBackupService.DataDirectory);
            var line =
                $"{DateTimeOffset.Now:u} [{category}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}" +
                $"{ex.StackTrace}{Environment.NewLine}---{Environment.NewLine}";
            File.AppendAllText(LogPath, line);
        }
        catch { /* never throw from logging */ }
    }

    public static void Write(string category, string message)
    {
        try
        {
            Directory.CreateDirectory(VaultBackupService.DataDirectory);
            File.AppendAllText(LogPath,
                $"{DateTimeOffset.Now:u} [{category}] {message}{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }
}
