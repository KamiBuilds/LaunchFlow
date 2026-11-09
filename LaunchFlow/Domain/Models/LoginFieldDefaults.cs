namespace LaunchFlow.Models;

/// <summary>
/// Built-in Auto login selectors. Remains the product default for new workflows
/// and the signal that automatic form classification is still allowed.
/// </summary>
public static class LoginFieldDefaults
{
    public const string UsernameSelector =
        "input[type='email'], input[name*='user' i], input[name*='email' i], #username, #email";

    public const string PasswordSelector =
        "input[type='password'], #password";
}
