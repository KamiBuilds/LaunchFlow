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

    public static string FailureMessage(string? code) =>
        RunIssueNotice.FromLoginCodeMessage(code);
}
