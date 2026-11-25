namespace LaunchFlow.Services;

/// <summary>
/// One eligibility policy for new master passwords, rotations, and new portable
/// exports. Existing vaults and backups are always verified cryptographically
/// with their original passwords, never re-assessed under today's policy.
/// </summary>
public static class PasswordStrength
{
    public const int MinimumLength = 12;

    public sealed record Assessment(int Score, string Label, string Hint, bool Acceptable);

    public static Assessment Evaluate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumLength)
            return new(0, "Too short", $"Use at least {MinimumLength} characters.", false);

        if (password.All(char.IsWhiteSpace))
            return new(0, "Weak", "Use real words or other characters, not spaces alone.", false);

        // Long numeric strings and passwords padded with spaces can satisfy
        // naive length/character-class scores without being hard to guess.
        if (password.All(char.IsDigit))
            return new(0, "Weak", "Avoid passwords made only of numbers.", false);
        if (password.Count(c => !char.IsWhiteSpace(c)) < 6)
            return new(0, "Weak", "Use more meaningful characters instead of padding with spaces.", false);

        if (IsSimpleRepeat(password))
            return new(0, "Weak", "Avoid repeating a single character or a two-character pattern.", false);

        if (IsCommon(password))
            return new(0, "Weak", "Avoid common passwords and predictable word-plus-number combinations.", false);

        var score = 2; // Length is an admission requirement, not an optional bonus.
        if (password.Length >= 16) score += 1;
        if (password.Any(char.IsLower) && password.Any(char.IsUpper)) score += 1;
        if (password.Any(char.IsDigit)) score += 1;
        if (password.Any(c => !char.IsLetterOrDigit(c))) score += 1;

        return score switch
        {
            <= 2 => new(score, "Fair",
                "Use a longer passphrase or add mixed case, a number, or a symbol.", false),
            3 => new(score, "Good",
                "Acceptable for a new vault. A longer, less predictable passphrase is recommended.", true),
            _ => new(score, "Strong",
                "Acceptable for a new LaunchFlow encrypted vault.", true)
        };
    }

    public static bool MeetsMinimum(string? password) => Evaluate(password).Acceptable;

    private static bool IsSimpleRepeat(string password)
    {
        for (var period = 1; period <= 2; period++)
        {
            if (password.Length % period != 0) continue;
            var repeat = true;
            for (var i = period; i < password.Length; i++)
                if (password[i] != password[i % period]) { repeat = false; break; }
            if (repeat) return true;
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
        if (banned.Any(b => lower.Contains(b, StringComparison.Ordinal))) return true;

        // Common Dutch/English words followed only by digits or predictable
        // decoration are not secure simply because the total length is 12+.
        foreach (var prefix in new[] { "test", "welkom", "welcome", "admin", "guest" })
        {
            if (!lower.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = lower.AsSpan(prefix.Length);
            if (rest.IsEmpty || rest.ToString().All(c =>
                    char.IsDigit(c) || c is '!' or '?' or '.' or '-' or '_'))
                return true;
        }
        return false;
    }
}
