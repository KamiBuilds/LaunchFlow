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
        SamePage => "Username + password on one page (GitHub-style)",
        EmailThenPassword => "Email first, then password (ThousandEyes-style)",
        CustomSelectors => "Custom field selectors (own site / npm-style)",
        Sso => "Browser SSO (no saved password fill)",
        _ => "Not learned yet — LaunchFlow will detect on the next successful Auto sign-in"
    };

    public static string FailureMessage(string? code) => code switch
    {
        "mfa" => "MFA / verification challenge detected — finish it yourself in the browser.",
        "sso-button" => "Only an SSO button is available — use Sign-in mode: Browser SSO, or finish sign-in manually.",
        "ambiguous-submit" => "Multiple Log In / Next buttons — LaunchFlow will not guess. Click the right one, then Fix sign-in if needed.",
        "no-form" => "Could not identify the sign-in form safely — continue in the browser.",
        "no-password-stage" => "Password stage never appeared after email/username — check the site or use Fix sign-in.",
        "no-password-field" => "Password field not found — complete sign-in in the browser or set custom selectors.",
        "missing-secrets" => "Missing saved username/password — update Settings → Passwords.",
        "no-final-submit" => "No owned final Sign In action could be verified — finish in the browser.",
        "origin-denied" => "Password page is on another site — approve that login origin, or sign in manually.",
        _ => "Could not verify sign-in — check the browser (MFA, SSO, or a changed login page)."
    };
}
