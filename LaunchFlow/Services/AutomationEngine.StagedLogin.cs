using System.Text.RegularExpressions;
using LaunchFlow.Models;
using Microsoft.Playwright;

namespace LaunchFlow.Services;

/// <summary>
/// Bounded built-in support for email-first/password-second login forms.
/// This is not a recorder or an SSO bypass: each credential remains bound to
/// the exact approved HTTPS document, and a foreign IdP still needs approval.
/// </summary>
public sealed partial class AutomationEngine
{
    private const string LoginStageButtonSelector =
        "button, input[type='submit'], input[type='button'], [role='button']";

    private static Task<bool> PasswordStageReadyAsync(
        WebsiteWorkflow workflow, IPage page, CancellationToken ct) =>
        LoginFieldReadyAsync(page, workflow.PasswordSelector, ct);

    private static async Task<bool> LoginFieldReadyAsync(
        IPage page, string selector, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(selector)) return false;
        var locator = page.Locator(selector);
        var count = Math.Min(await locator.CountAsync().ConfigureAwait(false), 24);
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var field = locator.Nth(i);
            if (await field.IsVisibleAsync().ConfigureAwait(false) &&
                await field.IsEditableAsync().ConfigureAwait(false))
                return true;
        }
        return false;
    }

    private async Task<bool> AdvanceToPasswordStageAsync(
        WebsiteWorkflow workflow, IPage page, string usernameOrigin, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireCredentialAccess();
        RequireAutomationUrl(page.Url);

        // A configured selector is an explicit user choice, but a missing one
        // must not force the user to record a typical "Next"/"Log In" button.
        IElementHandle? target = null;
        if (!string.IsNullOrWhiteSpace(workflow.UsernameContinueSelector))
        {
            var selected = page.Locator(workflow.UsernameContinueSelector);
            var count = Math.Min(await selected.CountAsync().ConfigureAwait(false), 24);
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (await selected.Nth(i).IsVisibleAsync().ConfigureAwait(false) &&
                    await selected.Nth(i).IsEnabledAsync().ConfigureAwait(false))
                {
                    target = await selected.Nth(i).ElementHandleAsync().ConfigureAwait(false);
                    break;
                }
            }
        }
        else
        {
            var buttons = page.Locator(LoginStageButtonSelector);
            var count = Math.Min(await buttons.CountAsync().ConfigureAwait(false), 32);
            var candidates = new List<IElementHandle>();
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var button = buttons.Nth(i);
                if (!await button.IsVisibleAsync().ConfigureAwait(false) ||
                    !await button.IsEnabledAsync().ConfigureAwait(false)) continue;
                // Only read the button label, never typed input values, full
                // document text, credentials, or an authentication response.
                var label = await button.EvaluateAsync<string>("""
                    el => (el.getAttribute('aria-label') || el.innerText ||
                           el.textContent || el.value || '').trim()
                    """).ConfigureAwait(false);
                if (!IsUsernameStageSubmitLabel(label)) continue;
                var element = await button.ElementHandleAsync().ConfigureAwait(false);
                if (element is not null) candidates.Add(element);
            }

            // Ambiguous sign-in buttons require the user's explicit selector;
            // guessing could unexpectedly initiate an SSO/other-account flow.
            if (candidates.Count == 1) target = candidates[0];
            else if (candidates.Count > 1)
                Log($"Multiple possible first-stage buttons for '{workflow.Name}'. Choose a continuation selector or sign in manually.");

            foreach (var candidate in candidates)
                if (!ReferenceEquals(candidate, target)) await candidate.DisposeAsync().ConfigureAwait(false);
        }
        if (target is null) return false;

        await using (target)
        {
            if (!await ClickApprovedLoginElementAsync(page, target, usernameOrigin, ct)
                .ConfigureAwait(false)) return false;
        }

        // Navigation can be same-document React state, another path, or a
        // different HTTPS identity-provider origin. We do not fill any password
        // until FillCredentialAsync independently approves that new origin.
        var end = DateTimeOffset.UtcNow.AddSeconds(
            Math.Min(20, Math.Max(5, workflow.LoginWaitSeconds)));
        while (DateTimeOffset.UtcNow < end)
        {
            ct.ThrowIfCancellationRequested();
            RequireCredentialAccess();
            RequireAutomationUrl(page.Url);
            try
            {
                if (await PasswordStageReadyAsync(workflow, page, ct).ConfigureAwait(false))
                {
                    Log($"Password stage is ready for '{workflow.Name}'.");
                    return true;
                }
            }
            catch (PlaywrightException) { /* page may be navigating */ }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return false;
    }

    // An explicit submit selector takes precedence. This fallback is used
    // only if that selector has no actionable button on the password page.
    private async Task<bool> ClickUnambiguousFinalSubmitAsync(
        WebsiteWorkflow workflow, IPage page, string? passwordOrigin, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireCredentialAccess();
        RequireAutomationUrl(page.Url);
        var buttons = page.Locator(LoginStageButtonSelector);
        var count = Math.Min(await buttons.CountAsync().ConfigureAwait(false), 32);
        var candidates = new List<IElementHandle>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var button = buttons.Nth(i);
                if (!await button.IsVisibleAsync().ConfigureAwait(false) ||
                    !await button.IsEnabledAsync().ConfigureAwait(false)) continue;
                var label = await button.EvaluateAsync<string>("""
                    el => (el.getAttribute('aria-label') || el.innerText ||
                           el.textContent || el.value || '').trim()
                    """).ConfigureAwait(false);
                if (!IsUsernameStageSubmitLabel(label) ||
                    label.Trim().Equals("continue to password", StringComparison.OrdinalIgnoreCase))
                    continue;

                // A button owned by another form (e.g. a header log-in action)
                // must not submit the just-filled password.
                var belongsToPasswordForm = await button.EvaluateAsync<bool>("""
                    el => {
                        const form = el.form || el.closest('form');
                        return !form || [...form.querySelectorAll('input[type="password"]')]
                            .some(input => input.getClientRects().length > 0);
                    }
                    """).ConfigureAwait(false);
                if (!belongsToPasswordForm) continue;

                var candidate = await button.ElementHandleAsync().ConfigureAwait(false);
                if (candidate is not null) candidates.Add(candidate);
            }
            if (candidates.Count != 1)
            {
                if (candidates.Count > 1)
                    Log($"Several possible password-stage buttons for '{workflow.Name}'. Choose a submit selector or sign in manually.");
                return false;
            }
            return await ClickApprovedLoginElementAsync(
                page, candidates[0], passwordOrigin, ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var candidate in candidates)
                await candidate.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Conservative automatic first-stage button admission.
    /// Never select social/SSO buttons, password reset, OTP, or MFA prompts.
    /// </summary>
    internal static bool IsUsernameStageSubmitLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        var normalized = Regex.Replace(label.Trim(), @"\s+", " ").ToLowerInvariant();
        if (normalized.Contains("sso", StringComparison.Ordinal) ||
            normalized.Contains("microsoft", StringComparison.Ordinal) ||
            normalized.Contains("google", StringComparison.Ordinal) ||
            normalized.Contains("forgot", StringComparison.Ordinal) ||
            normalized.Contains("reset", StringComparison.Ordinal) ||
            normalized.Contains("code", StringComparison.Ordinal) ||
            normalized.Contains("verify", StringComparison.Ordinal)) return false;
        return normalized is "log in" or "login" or "sign in" or "signin" or
            "next" or "continue" or "continue to password" or "proceed";
    }
}
