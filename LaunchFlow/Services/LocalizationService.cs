using System.Globalization;

namespace LaunchFlow.Services;

/// <summary>
/// English and Dutch UI foundation. Untranslated strings fall back to English;
/// no online translation or telemetry is used.
/// </summary>
public static class LocalizationService
{
    private static readonly IReadOnlyDictionary<string, string> Dutch =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Workflows"] = "Workflows",
            ["Passwords"] = "Wachtwoorden",
            ["Settings"] = "Instellingen",
            ["Save workspace"] = "Werkruimte opslaan",
            ["Run workspace"] = "Werkruimte starten",
            ["Run all"] = "Alles starten",
            ["Workspace profiles"] = "Werkruimtes",
            ["Language"] = "Taal"
        };

    public static string Text(string english, string? language = "auto")
    {
        var code = language is "nl" or "en"
            ? language
            : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return code == "nl" && Dutch.TryGetValue(english, out var translated)
            ? translated : english;
    }
}
