using Microsoft.Playwright;

namespace LaunchFlow.Services;

/// <summary>
/// Heuristics for “auto-login probably didn’t finish” (MFA, SSO wall, wrong selectors).
/// </summary>
public static class LoginHealth
{
    private static readonly string[] ChallengeHints =
    [
        "verify", "verification", "authenticate", "authentication", "mfa", "2fa", "two-factor",
        "one-time", "otp", "passcode", "security code", "approve sign-in", "duo", "okta verify",
        "enter code", "check your phone", "stay signed in", "captcha", "unusual activity"
    ];

    public sealed record Report(bool NeedsAttention, string Reason);

    public static async Task<Report> InspectAsync(IPage page, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var url = page.Url ?? "";
            var title = await page.TitleAsync().ConfigureAwait(false) ?? "";
            var bodyText = "";
            try
            {
                bodyText = await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions { Timeout = 2000 })
                    .ConfigureAwait(false) ?? "";
            }
            catch { /* ignore */ }

            var hay = (title + " " + url + " " + bodyText).ToLowerInvariant();
            foreach (var hint in ChallengeHints)
            {
                if (hay.Contains(hint, StringComparison.Ordinal))
                {
                    return new Report(true,
                        $"Sign-in looks incomplete (saw “{hint}”). Finish MFA/SSO in the browser, then use Teach / Repair login if fields moved.");
                }
            }

            var passwordVisible = await page.Locator("input[type='password']:visible").CountAsync().ConfigureAwait(false) > 0;
            if (passwordVisible)
            {
                return new Report(true,
                    "A password field is still on the page. Auto-login may have missed the selector, or another step is required.");
            }

            return new Report(false, "No obvious login challenge detected.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return new Report(true, "Could not verify sign-in. Check the browser before continuing.");
        }
    }
}
