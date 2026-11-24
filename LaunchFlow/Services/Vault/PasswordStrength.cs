namespace LaunchFlow.Services;

/// <summary>
/// Single acceptance policy for newly created master passwords (first-run,
/// recovery, and rotation) and new portable-export passwords.
/// Unlock/decrypt paths deliberately do not revalidate old passwords.
/// </summary>
public static class PasswordStrength
{
    public const int MinimumLength = 12;

    public sealed record Assessment(int Score, string Label, string Hint, bool Acceptable);

    public static Assessment Evaluate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumLength)
            return new(0, "Too short", $"Use at least {MinimumLength} characters.", false);

        // A space is useful *inside* a passphrase, but spaces alone provide no
        // secret. Never let length + punctuation scoring approve them.
        if (password.All(char.IsWhiteSpace))
            return new(0, "Weak", "Use words or other characters, not only spaces.", false);

        if (IsRepeatedPattern(password))
            return new(0, "Weak", "Avoid repeating the same character or short pattern.", false);

        if (IsCommon(password))
            return new(0, "Weak", "Avoid common or easily guessed passwords.", false);

        var score = 2; // Minimum length is mandatory, not merely a scoring bonus.
        if (password.Length >= 16) score += 1;
        if (password.Any(char.IsLower) && password.Any(char.IsUpper)) score += 1;
        if (password.Any(char.IsDigit)) score += 1;
        if (password.Any(c => !char.IsLetterOrDigit(c))) score += 1;

        return score switch
        {
            <= 2 => new(score, "Fair",
                "Add length, mixed case, a number, or a symbol. Fair is not enough for a new vault.", false),
            3 => new(score, "Good",
                "Acceptable for a local vault. A longer passphrase is recommended.", true),
            _ => new(score, "Strong",
                "Acceptable for a new LaunchFlow encrypted vault.", true)
        };
    }

    // One acceptance result. No second, diverging length/score check.
    public static bool MeetsMinimum(string? password) => Evaluate(password).Acceptable;

    private static bool IsRepeatedPattern(string password)
    {
        // Bound pattern length so very long pastes cannot cause quadratic work.
        for (var size = 1; size <= Math.Min(8, password.Length / 2); size++)
        {
            if (password.Length % size != 0) continue;
            var repeated = true;
            for (var index = size; index < password.Length; index++)
            {
                if (password[index] == password[index % size]) continue;
                repeated = false;
                break;
            }
            if (repeated) return true;
        }
        return false;
    }

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
