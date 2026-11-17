namespace LaunchFlow.Models;

/// <summary>
/// Built-in Auto login selectors. Remains the product default for new workflows
/// and the signal that automatic form classification is still allowed.
/// </summary>
public static class LoginFieldDefaults
{
    public const string UsernameSelector =
        "input[type='email'], input[name*='user' i], input[name*='email' i], input[name*='identity' i], " +
        "#username, #email, #identity, input[name='identity' i]";

    public const string PasswordSelector =
        "input[type='password'], #password, input[name='secret' i], #secret";

    // Includes common SPA login buttons (ThousandEyes uses type=button#submit-button).
    public const string SubmitSelector =
        "button[type='submit'], input[type='submit'], button#submit-button, button.submit-button";

    /// <summary>Pre-0.7.54 default still stored on many existing website rows.</summary>
    public const string LegacySubmitSelector =
        "button[type='submit'], input[type='submit']";

    public static bool IsBuiltInSubmitSelector(string? selector) =>
        string.Equals(selector, SubmitSelector, StringComparison.Ordinal) ||
        string.Equals(selector, LegacySubmitSelector, StringComparison.Ordinal);
}
