using System.IO;
using System.Text.RegularExpressions;

namespace LaunchFlow.Services;

/// <summary>
/// Local-only, best-effort diagnostic events. Never uploads or records exception
/// messages, stack traces, secrets, URLs, paths, or form values.
/// </summary>
public static class CrashLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static string LogPath =>
        Path.Combine(VaultDatabase.Current.DataDirectory, "diagnostics.log");

    public static void Write(string category, Exception ex)
    {
        if (ex is null) return;
        // Exception.Message and StackTrace can contain credentials or private URLs.
        Append(category, "Exception: " + SafeToken(ex.GetType().Name));
    }

    public static void Write(string category, string message)
    {
        // Arbitrary messages may contain private user data. Record an event code
        // only if the caller supplies a simple single-token identifier.
        Append(category, "Event: " + SafeToken(message));
    }

    private static string SafeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 64 ||
            !Regex.IsMatch(value, @"\A[A-Za-z][A-Za-z0-9_-]*\z"))
            return "redacted";
        return value;
    }

    private static void Append(string category, string eventCode)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(VaultDatabase.Current.DataDirectory);
                // Rotate before appending; never let diagnostics grow indefinitely.
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= MaxBytes)
                {
                    var previous = LogPath + ".previous";
                    File.Move(LogPath, previous, overwrite: true);
                }
                File.AppendAllText(LogPath,
                    $"{DateTimeOffset.UtcNow:O} [{SafeToken(category)}] {eventCode}{Environment.NewLine}");
            }
        }
        catch { /* diagnostics must never prevent the application from running */ }
    }
}
