using System.IO;
using System.Text;

namespace LaunchFlow.Services;

/// <summary>
/// Local, bounded, best-effort events. Only fixed known categories and event codes
/// are emitted; caller strings and exception objects are never serialized.
/// Callers retain responsibility for diagnostics consent.
/// </summary>
public static class CrashLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static string LogPath =>
        Path.Combine(VaultDatabase.Current.DataDirectory, "diagnostics.log");

    public static void Write(string category, Exception ex) => Append(KnownCategory(category), "Failure");

    // Compatibility overload: a single alphanumeric token can still be a password.
    // Discard every message, rather than attempting pattern-based secret detection.
    public static void Write(string category, string message) => Append(KnownCategory(category), "Event");

    private static string KnownCategory(string? category) => category switch
    {
        "UI" => "UI",
        "Fatal" => "Fatal",
        "CloseSessions" => "CloseSessions",
        "SealProfiles" => "SealProfiles",
        "ProfileSeal" => "ProfileSeal",
        "ProfileUnseal" => "ProfileUnseal",
        "ProfileDelete" => "ProfileDelete",
        "UpdateCheck" => "UpdateCheck",
        "VaultReset" => "VaultReset",
        "VaultResetDelete" => "VaultResetDelete",
        _ => "General"
    };

    private static void Append(string category, string eventCode)
    {
        try
        {
            lock (Gate)
            {
                var path = LogPath;
                var line = $"{DateTimeOffset.UtcNow:O} [{category}] {eventCode}{Environment.NewLine}";
                Directory.CreateDirectory(VaultDatabase.Current.DataDirectory);
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > MaxBytes)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch { /* A diagnostic failure must never break vault or application cleanup. */ }
    }
}
