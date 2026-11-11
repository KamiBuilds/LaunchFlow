namespace LaunchFlow.Services;

/// <summary>
/// Remembered per-website login shape. Auto still rediscovers when empty;
/// a remembered recipe prefers the path that already worked. CustomSelectors
/// never disables current-form classification while default selectors remain.
/// </summary>
public static class LoginRecipe
{
    public const string Unknown = "";
    public const string SamePage = "SamePage";
    public const string EmailThenPassword = "EmailThenPassword";
    public const string CustomSelectors = "CustomSelectors";
    public const string Sso = "Sso";

    public static string Normalize(string? value) => value switch
    {
        SamePage => SamePage,
        EmailThenPassword => EmailThenPassword,
        CustomSelectors => CustomSelectors,
        Sso => Sso,
        _ => Unknown
    };

    public static string DisplayName(string? value) => Normalize(value) switch
    {
        SamePage => "Email and password on the same page",
        EmailThenPassword => "Email first, then password on the next step",
        CustomSelectors => "Special field layout (set with help if needed)",
        Sso => "Company / browser sign-in (you finish in the browser)",
        _ => "Not learned yet — LaunchFlow remembers it after a successful sign-in"
    };

    public static string FailureMessage(string? code) => code switch
    {
        "mfa" => "The site asked for an extra code or approval — finish that in the browser window, then you’re done.",
        "sso-button" => "This site wants company sign-in — finish in the browser, or pick “Company / browser sign-in” for this website.",
        "ambiguous-submit" => "A few Sign in buttons are on the page — click the right one in the browser. Use Fix sign-in later if you want LaunchFlow to learn it.",
        "no-form" => "The sign-in boxes weren’t ready yet — check the browser window, or try Test again in a moment.",
        "no-password-stage" => "The password step didn’t show up yet — check the browser, or try again in a moment.",
        "no-password-field" => "Couldn’t spot the password box this time — finish in the browser, or try Test again.",
        "missing-secrets" => "No saved username/password for this site yet — add them under Settings → Passwords when you’re ready.",
        "no-final-submit" => "Sign-in may need one more click in the browser window.",
        "origin-denied" => "Password continues on another page — approve that once when asked, or finish sign-in in the browser.",
        _ => "Sign-in didn’t finish this time — peek at the browser window. Nothing else is broken."
    };
}
