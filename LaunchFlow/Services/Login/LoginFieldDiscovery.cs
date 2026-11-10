using LaunchFlow.Models;

namespace LaunchFlow.Services;

/// <summary>
/// Decides when Auto login may use classified field discovery versus explicit
/// custom selectors. Username and password decisions are independent: customizing
/// the username selector must not disable authentication-purpose classification
/// while the password selector remains the built-in default. A remembered
/// <see cref="LoginRecipe.CustomSelectors"/> value is only a preference label —
/// never permission to skip classification for any default selector.
/// </summary>
public static class LoginFieldDiscovery
{
    public const string StandardUsernameSelector = LoginFieldDefaults.UsernameSelector;
    public const string StandardPasswordSelector = LoginFieldDefaults.PasswordSelector;

    /// <summary>
    /// True when both selectors still use built-in defaults (legacy combined gate).
    /// Prefer the username/password-specific helpers for fill decisions.
    /// </summary>
    public static bool UsesAutomaticDiscovery(WebsiteWorkflow workflow) =>
        UsesAutomaticUsernameDiscovery(workflow) && UsesAutomaticPasswordDiscovery(workflow);

    public static bool UsesAutomaticUsernameDiscovery(WebsiteWorkflow workflow) =>
        UsesAutomaticUsernameDiscovery(workflow.UsernameSelector);

    public static bool UsesAutomaticUsernameDiscovery(string? usernameSelector) =>
        string.Equals(usernameSelector, StandardUsernameSelector, StringComparison.Ordinal);

    public static bool UsesAutomaticPasswordDiscovery(WebsiteWorkflow workflow) =>
        UsesAutomaticPasswordDiscovery(workflow.PasswordSelector);

    public static bool UsesAutomaticPasswordDiscovery(string? passwordSelector) =>
        string.Equals(passwordSelector, StandardPasswordSelector, StringComparison.Ordinal);
}
