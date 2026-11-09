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

    // A password input already bound to this document may be temporarily
    // disabled or hidden. Preserve that exact document binding rather than
    // clicking Next and retargeting credentials onto a replacement document.
    private static async Task<bool> PasswordStagePresentAsync(
        WebsiteWorkflow workflow, IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(workflow.PasswordSelector)) return false;
        return await page.Locator(workflow.PasswordSelector).CountAsync().ConfigureAwait(false) > 0;
    }

    private async Task<IElementHandle?> AdvanceToPasswordStageAsync(
        WebsiteWorkflow workflow, IPage page, CredentialFieldBinding usernameOwner,
        bool hadPasswordField, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireCredentialAccess();
        RequireAutomationUrl(page.Url);

        // Record all previously actionable password elements. Only reuse of
        // one of these elements can veto an apparent stage transition; an
        // unrelated persistent registration field cannot veto a new login form.
        var previouslyReadyPasswords = new List<IElementHandle>();
        try
        {
            var passwordCandidates = page.Locator(workflow.PasswordSelector);
            var passwordCount = await passwordCandidates.CountAsync().ConfigureAwait(false);
            if (passwordCount > 32) return null;
            for (var i = 0; i < passwordCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = passwordCandidates.Nth(i);
                if (await candidate.IsVisibleAsync().ConfigureAwait(false) &&
                    await candidate.IsEditableAsync().ConfigureAwait(false))
                {
                    var element = await candidate.ElementHandleAsync().ConfigureAwait(false);
                    if (element is not null) previouslyReadyPasswords.Add(element);
                }
            }
        }
        catch
        {
            foreach (var prior in previouslyReadyPasswords)
                await prior.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        try
        {
            // A configured selector is an explicit user choice, but a missing one
            // must not force the user to record a typical "Next"/"Log In" button.
            IElementHandle? target = null;
            var explicitSelector = !string.IsNullOrWhiteSpace(workflow.UsernameContinueSelector);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(
                Math.Min(15, Math.Max(3, workflow.LoginWaitSeconds)));
            do
            {
                ct.ThrowIfCancellationRequested();
                RequireCredentialAccess();
                RequireAutomationUrl(usernameOwner.Origin);
                if (explicitSelector)
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
                            if (target is not null) break;
                        }
                    }
                }
                else
                {
                    var buttons = page.Locator(LoginStageButtonSelector);
                    var count = Math.Min(await buttons.CountAsync().ConfigureAwait(false), 64);
                    var candidates = new List<IElementHandle>();
                    try
                    {
                        for (var i = 0; i < count; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            var button = buttons.Nth(i);
                            if (!await button.IsVisibleAsync().ConfigureAwait(false)) continue;
                            var label = await button.EvaluateAsync<string>("""
                                el => (el.getAttribute('aria-label') || el.innerText ||
                                       el.textContent || el.value || '').trim()
                                """).ConfigureAwait(false);
                            if (!IsUsernameStageSubmitLabel(label)) continue;
                            var element = await button.ElementHandleAsync().ConfigureAwait(false);
                            if (element is null) continue;
                            var owned = await element.EvaluateAsync<bool>(
                                "(button, form) => { const owner = 'form' in button ? button.form : button.closest('form'); return form !== null && owner === form; }",
                                usernameOwner.Form).ConfigureAwait(false);
                            if (owned) candidates.Add(element);
                            else await element.DisposeAsync().ConfigureAwait(false);
                        }
                        if (candidates.Count > 1)
                        {
                            Log($"Multiple possible first-stage actions for '{workflow.Name}'. Sign in manually.");
                            return null;
                        }
                        if (candidates.Count == 1 && await candidates[0].IsEnabledAsync().ConfigureAwait(false))
                        {
                            target = candidates[0];
                            candidates.Clear();
                        }
                    }
                    finally
                    {
                        foreach (var candidate in candidates)
                            await candidate.DisposeAsync().ConfigureAwait(false);
                    }
                }

                if (target is not null) break;
                if (DateTimeOffset.UtcNow >= deadline) return null;
                await Task.Delay(150, ct).ConfigureAwait(false);
            } while (true);

            await using (target)
            {
                if (!await ClickApprovedLoginElementAsync(
                    page, target, usernameOwner.Origin, ct, usernameOwner,
                    requireOwnedForm: !explicitSelector).ConfigureAwait(false)) return null;
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
                // When the original page already had a password control, a late
                // cross-origin navigation invalidates it. Never approve/fill a
                // replacement on the foreign document during that attempt.
                if (hadPasswordField && !OriginsMatch(usernameOwner.Origin, GetOrigin(page.Url)))
                    return null;
                try
                {
                    var automaticPassword = LoginFieldDiscovery.UsesAutomaticPasswordDiscovery(workflow) &&
                        LoginFieldDiscovery.UsesAutomaticUsernameDiscovery(workflow);
                    var nextPassword = automaticPassword
                        ? await ResolveSupportedResumedPasswordStageAsync(workflow, page, ct).ConfigureAwait(false) ??
                            await ResolvePasswordAfterOwnedContinuationAsync(
                                workflow, page, usernameOwner.Origin, ct).ConfigureAwait(false)
                        : await ResolveConfiguredPasswordStageAsync(workflow, page, ct).ConfigureAwait(false);
                    if (nextPassword is null)
                    {
                        await Task.Delay(250, ct).ConfigureAwait(false);
                        continue;
                    }
                    var transferred = false;
                    try
                    {
                        if (await nextPassword.IsVisibleAsync().ConfigureAwait(false) &&
                            await nextPassword.IsEditableAsync().ConfigureAwait(false))
                        {
                            var existedBeforeNext = false;
                            foreach (var prior in previouslyReadyPasswords)
                            {
                                try
                                {
                                    if (await nextPassword.EvaluateAsync<bool>(
                                        "(field, prior) => field === prior && field.isConnected", prior)
                                        .ConfigureAwait(false))
                                    {
                                        existedBeforeNext = true;
                                        break;
                                    }
                                }
                                catch (PlaywrightException) { /* old document navigated */ }
                            }
                            if (!existedBeforeNext)
                            {
                                Log($"Password stage is ready for '{workflow.Name}'.");
                                transferred = true;
                                return nextPassword;
                            }
                        }
                    }
                    finally
                    {
                        if (!transferred) await nextPassword.DisposeAsync().ConfigureAwait(false);
                    }
                }
                catch (PlaywrightException) { /* page may be navigating */ }
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            return null;
        }
        finally
        {
            foreach (var prior in previouslyReadyPasswords)
                await prior.DisposeAsync().ConfigureAwait(false);
        }
    }

    // This fallback is available only after an owned first-stage action has
    // actually advanced the login. It never applies to a password-only page or
    // generic email/newsletter field, where ordinary strict classification wins.
    private static async Task<IElementHandle?> ResolvePasswordAfterOwnedContinuationAsync(
        WebsiteWorkflow workflow, IPage page, string identifierOrigin, CancellationToken ct)
    {
        var matches = page.Locator(workflow.PasswordSelector);
        var count = await matches.CountAsync().ConfigureAwait(false);
        if (count == 0 || count > 16) return null;
        IElementHandle? selected = null;
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var locator = matches.Nth(i);
            if (!await locator.IsVisibleAsync().ConfigureAwait(false) ||
                !await locator.IsEditableAsync().ConfigureAwait(false)) continue;
            var field = await locator.ElementHandleAsync().ConfigureAwait(false);
            if (field is null) continue;
            var keep = false;
            try
            {
                var fieldOrigin = await field.EvaluateAsync<string>(
                    "field => field.ownerDocument.location.origin").ConfigureAwait(false);
                if (!OriginsMatch(identifierOrigin, fieldOrigin) &&
                    (string.IsNullOrWhiteSpace(workflow.PasswordCredentialOrigin) ||
                     !OriginsMatch(GetOrigin(workflow.PasswordCredentialOrigin), fieldOrigin)))
                    continue;
                var eligible = await field.EvaluateAsync<bool>("""
                    field => {
                      if (!(field instanceof HTMLInputElement) || field.type !== 'password' ||
                          !field.isConnected ||
                          field.ownerDocument.defaultView?.document !== field.ownerDocument ||
                          /new-password|one-time-code/i.test(field.autocomplete || '')) return false;
                      const form = field.form;
                      if (!form?.isConnected) return false;
                      const forbidden = /(?:sign[ -]?up|register|create.?account|forgot|reset|change.?password)/i;
                      if (forbidden.test([form.id, form.getAttribute('action'),
                          form.getAttribute('aria-label')].join(' '))) return false;
                      const passwords = Array.from(form.elements).filter(el =>
                        el instanceof HTMLInputElement && el.type === 'password');
                      return passwords.length === 1 && passwords[0] === field;
                    }
                    """).ConfigureAwait(false);
                if (!eligible || !await HasOwnedStageActionAsync(workflow, page, field, ct)
                    .ConfigureAwait(false)) continue;
                if (selected is not null)
                {
                    await selected.DisposeAsync().ConfigureAwait(false);
                    selected = null;
                    return null;
                }
                selected = field;
                keep = true;
            }
            finally
            {
                if (!keep) await field.DisposeAsync().ConfigureAwait(false);
            }
        }
        return selected;
    }

    // An explicit submit selector takes precedence. This fallback is used
    // only if that selector has no actionable button on the password page.
    private async Task<bool> ClickUnambiguousFinalSubmitAsync(
        WebsiteWorkflow workflow, IPage page, CredentialFieldBinding passwordOwner, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(
            Math.Min(15, Math.Max(3, workflow.LoginWaitSeconds)));
        do
        {
            ct.ThrowIfCancellationRequested();
            RequireCredentialAccess();
            RequireAutomationUrl(passwordOwner.Origin);
            var buttons = page.Locator(LoginStageButtonSelector);
            var count = Math.Min(await buttons.CountAsync().ConfigureAwait(false), 64);
            var candidates = new List<IElementHandle>();
            try
            {
                for (var i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var button = buttons.Nth(i);
                    if (!await button.IsVisibleAsync().ConfigureAwait(false)) continue;
                    var label = await button.EvaluateAsync<string>("""
                        el => (el.getAttribute('aria-label') || el.innerText ||
                               el.textContent || el.value || '').trim()
                        """).ConfigureAwait(false);
                    if (!IsUsernameStageSubmitLabel(label) ||
                        label.Trim().Equals("continue to password", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var belongs = await button.EvaluateAsync<bool>(
                        "(button, form) => { const owner = 'form' in button ? button.form : button.closest('form'); return form !== null && owner === form; }",
                        passwordOwner.Form).ConfigureAwait(false);
                    if (!belongs) continue;
                    var element = await button.ElementHandleAsync().ConfigureAwait(false);
                    if (element is not null) candidates.Add(element);
                }

                if (candidates.Count > 1)
                {
                    Log($"Several possible password-stage buttons for '{workflow.Name}'. Sign in manually.");
                    return false;
                }
                if (candidates.Count == 1)
                {
                    var actionable = await candidates[0].IsEnabledAsync().ConfigureAwait(false);
                    if (actionable)
                        return await ClickApprovedLoginElementAsync(
                            page, candidates[0], passwordOwner.Origin, ct,
                            passwordOwner, requireOwnedForm: true).ConfigureAwait(false);
                }
            }
            finally
            {
                foreach (var candidate in candidates)
                    await candidate.DisposeAsync().ConfigureAwait(false);
            }

            if (DateTimeOffset.UtcNow >= deadline) return false;
            await Task.Delay(150, ct).ConfigureAwait(false);
        } while (true);
    }

    private static async Task<bool> HasOwnedStageActionAsync(
        WebsiteWorkflow workflow, IPage page, IElementHandle passwordField, CancellationToken ct)
    {
        await using var owner = await passwordField.EvaluateHandleAsync("field => field.form").ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(workflow.SubmitSelector) &&
            workflow.SubmitSelector != StandardSubmitSelector)
        {
            var explicitlyConfigured = page.Locator(workflow.SubmitSelector);
            var configuredCount = Math.Min(await explicitlyConfigured.CountAsync().ConfigureAwait(false), 32);
            var ownedCount = 0;
            for (var i = 0; i < configuredCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var control = explicitlyConfigured.Nth(i);
                if (!await control.IsVisibleAsync().ConfigureAwait(false)) continue;
                if (await control.EvaluateAsync<bool>(
                    "(button, form) => { const owner = 'form' in button ? button.form : button.closest('form'); return form !== null && owner === form; }",
                    owner).ConfigureAwait(false))
                    ownedCount++;
            }
            return ownedCount == 1;
        }
        var controls = page.Locator(LoginStageButtonSelector);
        var count = Math.Min(await controls.CountAsync().ConfigureAwait(false), 64);
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var control = controls.Nth(i);
            if (!await control.IsVisibleAsync().ConfigureAwait(false)) continue;
            var owned = await control.EvaluateAsync<bool>(
                "(button, form) => { const owner = 'form' in button ? button.form : button.closest('form'); return form !== null && owner === form; }",
                owner).ConfigureAwait(false);
            if (!owned) continue;
            var label = await control.EvaluateAsync<string>(
                "el => (el.getAttribute('aria-label') || el.innerText || el.textContent || el.value || '').trim()")
                .ConfigureAwait(false);
            if (IsUsernameStageSubmitLabel(label)) return true;
        }
        return false;
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
