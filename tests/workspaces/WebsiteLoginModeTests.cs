using LaunchFlow.Services;
internal static class WebsiteLoginModeTests
{
    public static IEnumerable<(string Name, Action Body)> Cases()
    {
        yield return ("login mode indexes map Auto/None/SSO", () =>
        {
            Check(WebsiteLoginMode.Index("None") == 1 && WebsiteLoginMode.Index("SSO") == 2 &&
                WebsiteLoginMode.Index("Auto") == 0, "indexes");
            Check(WebsiteLoginMode.FromIndex(1) == "None" && WebsiteLoginMode.FromIndex(2) == "SSO", "from indexes");
        });
        yield return ("saved-password usage follows Auto mode only", () =>
        {
            Check(WebsiteLoginMode.UsesSavedPassword("Auto"), "Auto");
            Check(!WebsiteLoginMode.UsesSavedPassword("None") && !WebsiteLoginMode.UsesSavedPassword("SSO"), "no password");
        });
        yield return ("resolve prefers explicit mode over legacy AutoLogin flag", () =>
        {
            Check(WebsiteLoginMode.Resolve(null, false) == "None", "legacy false");
            Check(WebsiteLoginMode.Resolve(null, true) == "Auto", "legacy true");
            Check(WebsiteLoginMode.Resolve("SSO", true) == "SSO", "explicit SSO");
            Check(WebsiteLoginMode.Resolve("none", true) == "None", "case-insensitive None");
        });
        yield return ("post-create guidance always points at Test this site", () =>
        {
            var first = WebsiteNextStepGuidance.ForCreatedWebsite("Ops board", false, firstWebsite: true);
            Check(first.Contains("Test this site") && first.Contains("Passwords") &&
                first.Contains("Record clicks") && first.Contains("Run all"), "guidance");
            Check(WebsiteNextStepGuidance.DialogTitle(true) == "Website ready", "first");
            Check(WebsiteNextStepGuidance.DialogTitle(false) == "Website added", "next");
        });
    }
    private static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
}
