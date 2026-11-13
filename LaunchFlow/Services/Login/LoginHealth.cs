using Microsoft.Playwright;

namespace LaunchFlow.Services;

/// <summary>
/// Heuristics for “auto-login probably didn’t finish” (MFA, SSO wall, wrong selectors).
/// </summary>
public static class LoginHealth
{
    private static readonly string[] ChallengeHints =
    [
        "verify", "verification", "mfa", "2fa", "two-factor",
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
            var interactive = await InspectAsync(page, ct).ConfigureAwait(false);
            if (interactive.NeedsAttention) return interactive;
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
            // A changed URL and nonempty body are not authentication proof.
            // Recognize only current account/navigation evidence; otherwise a
            // user confirms in the browser before downstream actions execute.
            var evidence = page.Locator(
                "#signed-in, [data-authenticated='true'], [data-login-state='authenticated'], " +
                "[data-testid='user-menu'], [data-testid='account-menu'], " +
                "a[href*='/logout' i], button[aria-label='Account'], button[aria-label='User menu']");
            var evidenceCount = Math.Min(await evidence.CountAsync().ConfigureAwait(false), 24);
            for (var i = 0; i < evidenceCount; i++)
                if (await evidence.Nth(i).IsVisibleAsync().ConfigureAwait(false)) return true;
            return await HasAuthenticatedNpmNavigationAsync(page, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    public static async Task<bool> VerifiedApplicationSessionAsync(
        IPage page, string expectedApplicationUrl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (!Uri.TryCreate(expectedApplicationUrl, UriKind.Absolute, out var expected) ||
                !Uri.TryCreate(page.Url, UriKind.Absolute, out var actual) ||
                actual.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(expected.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(expected.Authority, actual.Authority, StringComparison.OrdinalIgnoreCase))
                return false;

            if ((await InspectAsync(page, ct).ConfigureAwait(false)).NeedsAttention) return false;
            if (await page.Locator("input[autocomplete='one-time-code']:visible, input[type='password']:visible")
                    .CountAsync().ConfigureAwait(false) > 0)
                return false;

            var accountControls = page.Locator(
                "button[aria-label='Account'], button[aria-label='User menu'], " +
                "[data-testid='user-menu'], [data-testid='account-menu']");
            var signOutControls = page.Locator(
                "a[href*='/logout' i], a[href*='/signout' i], " +
                "button[aria-label='Sign out'], button[aria-label='Log out']");
            var accountVisible = false;
            var signOutVisible = false;
            var accountCount = Math.Min(await accountControls.CountAsync().ConfigureAwait(false), 24);
            for (var i = 0; i < accountCount; i++)
                if (await accountControls.Nth(i).IsVisibleAsync().ConfigureAwait(false))
                {
                    accountVisible = true;
                    break;
                }
            var signOutCount = Math.Min(await signOutControls.CountAsync().ConfigureAwait(false), 24);
            for (var i = 0; i < signOutCount; i++)
                if (await signOutControls.Nth(i).IsVisibleAsync().ConfigureAwait(false))
                {
                    signOutVisible = true;
                    break;
                }
            return (accountVisible && signOutVisible) ||
                await HasAuthenticatedNpmNavigationAsync(page, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    private static async Task<bool> HasAuthenticatedNpmNavigationAsync(IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Never accept a redirected URL alone as login proof. Older NPM keeps
        // Log out inside a closed dropdown; newer builds vary the favicon path
        // and header markup — accept several stable signed-in signals.
        if (await page.Locator("input[type='password']:visible, input[autocomplete='one-time-code']:visible")
                .CountAsync().ConfigureAwait(false) > 0)
            return false;

        var classic = await page.Locator("a.navbar-brand[href='/']:visible").CountAsync().ConfigureAwait(false) == 1 &&
               await page.Locator("a.navbar-brand[href='/'] img[src*='favicon']")
                   .CountAsync().ConfigureAwait(false) >= 1 &&
               await page.Locator(".dropdown > a.nav-link:visible:has(.avatar), .dropdown > a.nav-link:visible:has(.user)")
                   .CountAsync().ConfigureAwait(false) >= 1 &&
               await page.Locator("a.dropdown-item.logout[href='/logout'], a.dropdown-item[href='/logout']")
                   .CountAsync().ConfigureAwait(false) >= 1;
        if (classic) return true;

        // Modern npm-style dashboards: proxy hosts / users nav with an account menu.
        var modernNav = await page.Locator(
                "a[href='/']:visible, a[href$='/hosts']:visible, a[href*='proxy']:visible, " +
                "nav a:has-text('Proxy Hosts'):visible, nav a:has-text('Dashboard'):visible")
            .CountAsync().ConfigureAwait(false);
        var account = await page.Locator(
                "[data-testid='user-menu']:visible, [data-testid='account-menu']:visible, " +
                "button[aria-label='Account']:visible, button[aria-label='User menu']:visible, " +
                ".dropdown > a.nav-link:visible:has(.avatar)")
            .CountAsync().ConfigureAwait(false);
        var logout = await page.Locator(
                "a[href='/logout'], a[href*='logout' i], button[aria-label='Sign out'], button[aria-label='Log out']")
            .CountAsync().ConfigureAwait(false);
        return modernNav >= 1 && account >= 1 && logout >= 1;
    }

    /// <summary>
    /// Structural OTP / authenticator / passkey challenge evidence only.
    /// Shared with manual confirmation; excludes dashboard text heuristics.
    /// </summary>
    public static async Task<bool> HasStructuralSecurityChallengeAsync(IPage page, CancellationToken ct = default)
    {
        if (await page.Locator(
                "input[autocomplete='one-time-code']:visible, input[name='otp' i]:visible, input[type='password']:visible")
                .CountAsync().ConfigureAwait(false) > 0)
            return true;
        return await HasInteractiveSecurityChallengeAsync(page, ct).ConfigureAwait(false);
    }

    private static async Task<bool> HasInteractiveSecurityChallengeAsync(IPage page, CancellationToken ct)
    {
        var inputs = page.Locator("input:visible, textarea:visible, [role='textbox']:visible");
        var count = Math.Min(await inputs.CountAsync().ConfigureAwait(false), 48);
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var field = inputs.Nth(i);
            var interactive = await field.EvaluateAsync<bool>("""
                el => {
                    if (!el.isConnected || !el.getClientRects().length) return false;
                    const metadata = [
                        el.getAttribute('id'), el.getAttribute('name'),
                        el.getAttribute('autocomplete'), el.getAttribute('aria-label'),
                        el.getAttribute('placeholder')
                    ].filter(Boolean).join(' ').toLowerCase();
                    const labels = ('labels' in el && el.labels)
                        ? Array.from(el.labels).map(x => x.innerText || '').join(' ').toLowerCase()
                        : '';
                    const root = ('form' in el && el.form) || el.closest('form,[role="dialog"],[role="form"]');
                    const rootText = root && root.getClientRects().length
                        ? (root.innerText || '').slice(0, 350).toLowerCase()
                        : '';
                    const combined = metadata + ' ' + labels;
                    const codeIdentity = /(?:otp|one[- ]?time|passcode|security.?code|verif(?:y|ication).?code|auth(?:entication)?.?code|mfa|2fa)/i;
                    const codePrompt = /(?:enter|type|input|provide|confirm|verification|security|authenticator|one[- ]?time|passcode).{0,40}(?:code|number|token)/i;
                    const clearlyCode = codeIdentity.test(combined) ||
                        (/^(?:code|pin|token)$/i.test(labels.trim()) && codePrompt.test(rootText));
                    const numeric = /^(?:numeric|decimal|tel)$/i.test(el.getAttribute('inputmode') || '') ||
                        el.getAttribute('type') === 'tel' || el.getAttribute('autocomplete') === 'one-time-code';
                    return clearlyCode && (numeric || codePrompt.test(rootText) ||
                        /otp|one[- ]?time|security.?code|verification.?code|passcode/i.test(combined));
                }
                """).ConfigureAwait(false);
            if (interactive) return true;
        }

        var authenticators = page.Locator(
            "form:visible button:visible, [role='dialog']:visible button:visible, " +
            "[role='form']:visible button:visible");
        count = Math.Min(await authenticators.CountAsync().ConfigureAwait(false), 48);
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (await authenticators.Nth(i).EvaluateAsync<bool>("""
                button => {
                    if (!button.isConnected || button.disabled) return false;
                    const label = (button.getAttribute('aria-label') || button.innerText || '').trim();
                    return /^(?:approve sign[- ]?in|use passkey|verify with security key|enter verification code|complete captcha)$/i.test(label);
                }
                """).ConfigureAwait(false)) return true;
        }
        return false;
    }

    public static async Task<Report> InspectAsync(IPage page, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            // Only current interactive challenge cues and prominent headings
            // count. Footer/help/marketing text can mention MFA harmlessly.
            if (await page.Locator("input[autocomplete='one-time-code']:visible, input[name='otp' i]:visible")
                    .CountAsync().ConfigureAwait(false) > 0 ||
                await HasInteractiveSecurityChallengeAsync(page, ct).ConfigureAwait(false))
                return new Report(true,
                    "MFA / verification challenge detected — finish it yourself in the browser.");

            var title = await page.TitleAsync().ConfigureAwait(false) ?? "";
            var headings = page.Locator("h1, h2, h3, [role='alert'], [role='dialog']");
            var headingCount = Math.Min(await headings.CountAsync().ConfigureAwait(false), 32);
            var prominent = title.ToLowerInvariant();
            for (var i = 0; i < headingCount; i++)
            {
                var heading = headings.Nth(i);
                if (await heading.IsVisibleAsync().ConfigureAwait(false))
                    prominent += " " + (await heading.InnerTextAsync().ConfigureAwait(false)).ToLowerInvariant();
            }
            if (prominent.Contains("account locked", StringComparison.Ordinal) ||
                prominent.Contains("too many attempts", StringComparison.Ordinal))
                return new Report(true, "Account is temporarily locked. Stop automatic sign-in.");
            if (prominent.Contains("invalid password", StringComparison.Ordinal) ||
                prominent.Contains("incorrect password", StringComparison.Ordinal) ||
                prominent.Contains("invalid credentials", StringComparison.Ordinal))
                return new Report(true, "Saved sign-in details were rejected. Update the website password.");
            if (prominent.Contains("access denied", StringComparison.Ordinal) ||
                prominent.Contains("permission denied", StringComparison.Ordinal) ||
                prominent.Contains("unauthorized", StringComparison.Ordinal))
                return new Report(true, "This account cannot access the application.");
            foreach (var hint in ChallengeHints)
            {
                if (prominent.Contains(hint, StringComparison.Ordinal))
                    return new Report(true,
                        "MFA / verification challenge detected — finish it yourself in the browser.");
            }

            var passwordVisible = await page.Locator("input[type='password']:visible").CountAsync().ConfigureAwait(false) > 0;
            if (passwordVisible)
            {
                return new Report(true,
                    "Password stage still visible — Auto may need Fix sign-in, or finish the password step yourself.");
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
