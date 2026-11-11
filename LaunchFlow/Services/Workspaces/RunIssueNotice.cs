namespace LaunchFlow.Services;

/// <summary>Known, nonsecret reasons for a run needing attention; no inferred password failure.</summary>
public enum RunIssueCode
{
    Unknown, BrowserUnavailable, FieldsNotReady, PasswordStageMissing,
    PasswordFieldMissing, CredentialMissing, OriginDeclined, DestinationChanged,
    DocumentChanged, ExtraAuthenticationObserved, SsoManual, AmbiguousSubmit,
    FinalSubmitMissing, CompletionUnverified, Cancelled, RecordedStepFailed
}

/// <summary>Bounded user-facing notices; never forwards browser-driver exceptions,
/// full URLs, credentials, private labels or raw DOM payloads.</summary>
public static class RunIssueNotice
{
    public static RunIssueCode FromLoginCode(string? code) => code switch
    {
        "mfa" => RunIssueCode.ExtraAuthenticationObserved,
        "sso-button" => RunIssueCode.SsoManual,
        "ambiguous-submit" => RunIssueCode.AmbiguousSubmit,
        "no-form" => RunIssueCode.FieldsNotReady,
        "no-password-stage" => RunIssueCode.PasswordStageMissing,
        "no-password-field" => RunIssueCode.PasswordFieldMissing,
        "missing-secrets" => RunIssueCode.CredentialMissing,
        "no-final-submit" => RunIssueCode.FinalSubmitMissing,
        "origin-denied" => RunIssueCode.OriginDeclined,
        _ => RunIssueCode.Unknown
    };

    public static string Message(RunIssueCode code) => code switch
    {
        RunIssueCode.BrowserUnavailable => "Couldn't open the automation browser — check Browser readiness, or choose another installed browser.",
        RunIssueCode.FieldsNotReady => "The sign-in boxes weren’t ready yet — check the browser window, or try Test again in a moment.",
        RunIssueCode.PasswordStageMissing => "The password step didn’t show up yet — check the browser, or try again in a moment.",
        RunIssueCode.PasswordFieldMissing => "Couldn’t spot the password box this time — finish in the browser, or try Test again.",
        RunIssueCode.CredentialMissing => "No saved username/password for this site yet — add them under Settings → Passwords when you’re ready.",
        RunIssueCode.OriginDeclined => "Password continues on another page — approve that once when asked, or finish sign-in in the browser.",
        RunIssueCode.DestinationChanged => "The password destination changed during sign-in — check the page and try again.",
        RunIssueCode.DocumentChanged => "The sign-in form changed before filling — finish in the browser or retry.",
        RunIssueCode.ExtraAuthenticationObserved => "The site asked for an extra code or approval — finish that in the browser window, then you’re done.",
        RunIssueCode.SsoManual => "This site wants company sign-in — finish in the browser, or pick “Company / browser sign-in” for this website.",
        RunIssueCode.AmbiguousSubmit => "A few Sign in buttons are on the page — click the right one in the browser. Use Fix sign-in later if you want LaunchFlow to learn it.",
        RunIssueCode.FinalSubmitMissing => "Sign-in may need one more click in the browser window.",
        RunIssueCode.CompletionUnverified => "Sign-in could not be confirmed — check whether the website is signed in.",
        RunIssueCode.Cancelled => "Stopped at your request. You can try again when ready.",
        RunIssueCode.RecordedStepFailed => "Sign-in finished, but a recorded click needs a quick look.",
        _ => "Couldn't finish this time — check the browser window and try again when ready."
    };

    public static string FromLoginCodeMessage(string? code) => Message(FromLoginCode(code));

    public static bool PreserveLoginFailure(string? code) => code is
        "origin-denied" or "missing-secrets" or "no-password-stage" or
        "no-password-field" or "ambiguous-submit" or "no-final-submit";

    public static string SafeReportedReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return Message(RunIssueCode.Unknown);
        foreach (RunIssueCode code in Enum.GetValues<RunIssueCode>())
            if (string.Equals(reason, Message(code), StringComparison.Ordinal))
                return Message(code);
        return reason switch
        {
            "MFA / verification challenge detected — finish it yourself in the browser." =>
                Message(RunIssueCode.ExtraAuthenticationObserved),
            "Password stage still visible — Auto may need Fix sign-in, or finish the password step yourself." =>
                Message(RunIssueCode.PasswordStageMissing),
            "Could not verify sign-in. Check the browser before continuing." or
            "Login verification failed. Check the browser before continuing." or
            "The configured login success element is not visible." or
            "The expected signed-in URL was not reached." =>
                Message(RunIssueCode.CompletionUnverified),
            "Account is temporarily locked. Stop automatic sign-in." =>
                "The website reports an account lock — stop automatic sign-in and check the account.",
            "Saved sign-in details were rejected. Update the website password." =>
                "The website rejected the saved sign-in details — update them before retrying.",
            "This account cannot access the application." =>
                "The website reports access denied — check with the account administrator.",
            _ when reason.Contains("Firefox", StringComparison.OrdinalIgnoreCase) &&
                   (reason.Contains("automation", StringComparison.OrdinalIgnoreCase) ||
                    reason.Contains("Browser readiness", StringComparison.OrdinalIgnoreCase)) =>
                Message(RunIssueCode.BrowserUnavailable),
            _ => Message(RunIssueCode.Unknown)
        };
    }

    public static string SafeProgress(string? message) => message switch
    {
        "Opening browser…" or "Opening the website…" or
        "Using the window already open…" or "Looking for the sign-in page…" or
        "Waiting for sign-in boxes…" or "Signing in…" or
        "Checking that sign-in worked…" => message!,
        _ => "Working…"
    };
}
