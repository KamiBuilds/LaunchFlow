namespace LaunchFlow.Services;

/// <summary>
/// Lightweight master-password guidance. Does not replace a password manager;
/// only blocks obviously weak vault keys.
/// </summary>
public static class PasswordStrength
{
    public const int MinimumLength = 12;

    public sealed record Assessment(int Score, string Label, string Hint, bool Acceptable);

    public static Assessment Evaluate(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return new(0, "Too short", $"Use at least {MinimumLength} characters.", false);

        var score = 0;
        if (password.Length >= MinimumLength) score += 2;
        else if (password.Length >= 10) score += 1;
        if (password.Length >= 16) score += 1;
        if (password.Any(char.IsLower) && password.Any(char.IsUpper)) score += 1;
        if (password.Any(char.IsDigit)) score += 1;
        if (password.Any(c => !char.IsLetterOrDigit(c))) score += 1;
        if (IsCommon(password)) score = Math.Min(score, 1);

        return score switch
        {
            <= 1 => new(score, "Weak",
                $"Use {MinimumLength}+ characters with mixed case, a number, and a symbol.", false),
            2 => new(score, "Fair",
                "Add mixed case, a number, or a symbol — Fair is not enough for the vault key.", false),
            3 => new(score, "Good",
                "Solid for a local vault. Consider a longer passphrase.", true),
            _ => new(score, "Strong",
                "Good master password for LaunchFlow’s local encrypted database.", true)
        };
    }

    public static bool MeetsMinimum(string? password) =>
        !string.IsNullOrEmpty(password) &&
        password.Length >= MinimumLength &&
        Evaluate(password).Score >= 3;

    private static bool IsCommon(string password)
    {
        var lower = password.ToLowerInvariant();
        string[] banned =
        [
            "password", "password123", "launchflow", "1234567890", "qwertyuiop",
            "letmein1234", "changeme123", "iloveyou123"
        ];
        return banned.Any(b => lower.Contains(b, StringComparison.Ordinal));
    }
}
