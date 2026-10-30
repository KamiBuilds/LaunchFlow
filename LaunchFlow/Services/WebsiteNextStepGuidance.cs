namespace LaunchFlow.Services;
public static class WebsiteNextStepGuidance
{
    public static string ForCreatedWebsite(string websiteName, bool hasSavedLogin, bool firstWebsite)
    {
        var name = string.IsNullOrWhiteSpace(websiteName) ? "your website" : websiteName.Trim();
        var login = hasSavedLogin ? "A login was saved in Settings → Passwords." :
            "If it needs a password later, open Settings → Passwords. For local dashboards, leave login off.";
        var record = "Use Record clicks only when LaunchFlow should press buttons after the page opens.";
        var test = $"Next: select “{name}” and click Test this site.";
        if (firstWebsite)
            return $"{test}\n\n{login}\n{record}\n\nLater, Run all opens every enabled website. Change the shortcut under Settings → Shortcuts.";
        return $"{test}\n\n{login}\n{record}";
    }
    public static string DialogTitle(bool firstWebsite) => firstWebsite ? "Website ready" : "Website added";
}
