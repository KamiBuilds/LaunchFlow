using System.Globalization;

namespace LaunchFlow.Services;

/// <summary>Offline English/Dutch UI foundation. Unknown strings remain English.</summary>
public static class LocalizationService
{
    public static string ActiveLanguage { get; set; } = "auto";

    private static readonly IReadOnlyDictionary<string, string> Dutch =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Workflows"] = "Workflows",
            ["Websites"] = "Websites",
            ["Passwords"] = "Wachtwoorden",
            ["Settings"] = "Instellingen",
            ["Save workspace"] = "Werkruimte opslaan",
            ["Preview"] = "Voorbeeld",
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
            ["Stop"] = "Stoppen",
            ["Workflow step"] = "Workflowstap",
            ["Type"] = "Type",
            ["Target (URL, CSS selector, key, or milliseconds)"] = "Doel (URL, CSS-selector, toets of milliseconden)",
            ["Value (for Fill)"] = "Waarde (voor invullen)",
            ["Description (optional)"] = "Beschrijving (optioneel)",
            ["Never store passwords or access tokens as Fill values. Use the Passwords tab."] =
                "Sla geen wachtwoorden of toegangstokens op als invulwaarden. Gebruik het tabblad Wachtwoorden.",
            ["Cancel"] = "Annuleren",
            ["Save"] = "Opslaan",
            ["Navigate"] = "Navigeren",
            ["Click"] = "Klikken",
            ["Fill"] = "Invullen",
            ["Wait"] = "Wachten",
            ["Press"] = "Toets indrukken",
            ["AssertVisible"] = "Element verifiëren",
            ["WaitForURL"] = "Wachten op URL",
            ["Browser readiness"] = "Browsergereedheid",
            ["Microsoft Edge website"] = "Microsoft Edge-website",
            ["Google Chrome website"] = "Google Chrome-website",
            ["Check again"] = "Opnieuw controleren"

        };

    public static string ResolveLanguage(string? language = "auto")
    {
        var code = language is null or "auto"
            ? ActiveLanguage is "nl" or "en"
                ? ActiveLanguage
                : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : language;
        return code == "nl" ? "nl" : "en";
    }

    public static string Text(string english, string? language = "auto") =>
        ResolveLanguage(language) == "nl" && Dutch.TryGetValue(english, out var translated) ? translated : english;
}
