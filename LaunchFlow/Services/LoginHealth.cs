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

    public static async Task<Report> VerifySuccessAsync(
        IPage page, string successSelector, string successUrlContains, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(successSelector) && string.IsNullOrWhiteSpace(successUrlContains))
            return new Report(true, "Configure a login success selector or URL condition before running follow-up steps.");

        try
        {
            if (!string.IsNullOrWhiteSpace(successSelector))
            {
                var locator = page.Locator(successSelector).First;
                if (await locator.CountAsync().ConfigureAwait(false) == 0 ||
                    !await locator.IsVisibleAsync().ConfigureAwait(false))
                    return new Report(true, "The configured login success element is not visible.");
            }
            if (!string.IsNullOrWhiteSpace(successUrlContains) &&
                !page.Url.Contains(successUrlContains, StringComparison.OrdinalIgnoreCase))
                return new Report(true, "The expected signed-in URL was not reached.");

            return new Report(false, "Configured login success conditions passed.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return new Report(true, "Login verification failed. Check the browser before continuing.");
        }
    }

    /// <summary>
    /// Recognize a completed form submission when an outdated configured
    /// success selector never appears. Only accept a real page transition to
    /// a non-login destination with the password challenge gone; a blank
    /// page, failed login or MFA prompt is never accepted as signed in.
    /// This is intentionally separate from explicit post-login-step verification.
    /// </summary>
    public static async Task<bool> VerifiedRedirectAsync(
        IPage page, string loginUrl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (!Uri.TryCreate(loginUrl, UriKind.Absolute, out var before) ||
                !Uri.TryCreate(page.Url, UriKind.Absolute, out var after) ||
                after.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(before.Scheme, after.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(before.Authority, after.Authority, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(before.PathAndQuery, after.PathAndQuery, StringComparison.OrdinalIgnoreCase))
                return false;

            var path = after.AbsolutePath.Trim('/').ToLowerInvariant();
            if (path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment =>
                segment is "login" or "signin" or "sign-in" or "logout" or "auth"
                or "authenticate" or "verify" or "challenge" or "mfa" or "2fa"
                or "error" or "failed" or "denied" or "forbidden" or "unauthorized"))
                return false;

            // Browser navigation may have completed before application state
            // updates. A visible challenge cannot be mistaken for success.
            if (await page.Locator("input[type='password']:visible").CountAsync().ConfigureAwait(false) > 0 ||
                await page.Locator("input[autocomplete='one-time-code']:visible").CountAsync().ConfigureAwait(false) > 0)
                return false;
            var health = await InspectAsync(page, ct).ConfigureAwait(false);
            if (health.NeedsAttention) return false;
            var body = page.Locator("body");
            return await body.CountAsync().ConfigureAwait(false) > 0 &&
                !string.IsNullOrWhiteSpace(await body.InnerTextAsync(new LocatorInnerTextOptions
                {
                    Timeout = 2000
                }).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

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
