using System.Globalization;

namespace LaunchFlow.Services;

/// <summary>Offline English/Dutch UI foundation. Unknown strings remain English.</summary>
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
            ["Language"] = "Taal",
            ["Application language"] = "Taal van de toepassing",
            ["Run all enabled websites"] = "Alle ingeschakelde websites starten",
            ["Run selected workspace"] = "Geselecteerde werkruimte starten",
            ["Choose or type a workspace name"] = "Kies of typ een werkruimtenaam",
            ["Save enabled websites as a workspace"] = "Ingeschakelde websites opslaan als werkruimte",
            ["Workflow name"] = "Naam van de workflow",
            ["Website start URL"] = "Start-URL van de website",
            ["Screen"] = "Scherm",
            ["Workflow status"] = "Workflowstatus",
            ["Enable or disable this workflow"] = "Deze workflow in- of uitschakelen",
            ["Workflow steps after sign-in"] = "Workflowstappen na aanmelden",
            ["Ready"] = "Gereed",
            ["Running"] = "Bezig",
            ["Stopped"] = "Gestopt",
            ["Finished"] = "Voltooid",
            ["Failed"] = "Mislukt",
            ["Sign-in needs attention"] = "Aanmelden vereist aandacht",
            ["Stop"] = "Stoppen"
        };

    public static string ResolveLanguage(string? language = "auto")
    {
        var code = language is null or "auto"
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : language;
        return code == "nl" ? "nl" : "en";
    }

    public static string Text(string english, string? language = "auto") =>
        ResolveLanguage(language) == "nl" && Dutch.TryGetValue(english, out var translated) ? translated : english;
}
