using System.Text.RegularExpressions;
using LaunchFlow.Models;
using Microsoft.Playwright;

namespace LaunchFlow.Services;

public sealed partial class AutomationEngine
{
    private const string StandardUsernameSelector =
        "input[type='email'], input[name*='user' i], input[name*='email' i], #username, #email";
    private const string StandardSubmitSelector = "button[type='submit'], input[type='submit']";
    private const string AutomaticUsernameCandidates =
        "input[autocomplete~='username' i], input[name='login' i], #login_field, " +
        "input[type='email'], input[name*='user' i], input[name*='email' i], #username, #email";

    // Discover only existing same-origin HTTPS links. No guessed /login paths,
    // SSO buttons, cross-origin redirects or free-form page-wide click search.
    private async Task<bool> TryOpenLoginEntryAsync(
        WebsiteWorkflow workflow, IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var current) ||
            current.Scheme != Uri.UriSchemeHttps) return false;
        if (await PasswordStagePresentAsync(workflow, page, ct).ConfigureAwait(false))
            return false;

        // If the current page has an intended identifier form, do not
        // mistake a header's sign-in link for the current stage's continuation.
        if (await HasOwnedIdentifierStageAsync(workflow, page, ct).ConfigureAwait(false))
            return false;

        var anchors = page.Locator("a[href]");
        var count = Math.Min(await anchors.CountAsync().ConfigureAwait(false), 100);
        var destinations = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var anchor = anchors.Nth(i);
            if (!await anchor.IsVisibleAsync().ConfigureAwait(false) ||
                !await anchor.IsEnabledAsync().ConfigureAwait(false)) continue;
            var label = await anchor.EvaluateAsync<string>(
                "a => (a.getAttribute('aria-label') || a.innerText || '').trim()")
                .ConfigureAwait(false);
            if (!IsLocalSignInLabel(label)) continue;
            var href = await anchor.EvaluateAsync<string>("a => a.href").ConfigureAwait(false);
            if (!Uri.TryCreate(href, UriKind.Absolute, out var target) ||
                target.Scheme != Uri.UriSchemeHttps ||
                !OriginsMatch(GetOrigin(current.AbsoluteUri), GetOrigin(target.AbsoluteUri)))
                continue;
            RequireAutomationUrl(target.AbsoluteUri);
            destinations.Add(target.AbsoluteUri);
            if (destinations.Count > 1) break;
        }
        if (destinations.Count != 1)
        {
            if (destinations.Count > 1)
            {
                Log($"Could not identify one sign-in entry for '{workflow.Name}'. Continue in the browser.");
                return false;
            }
            return await TryOpenLoginDialogAsync(workflow, page, ct).ConfigureAwait(false);
        }

        var destination = destinations.Single();
        ct.ThrowIfCancellationRequested();
        RequireAutomationUrl(page.Url);
        RequireAutomationUrl(destination);
        Log($"Opening sign-in page for '{workflow.Name}'…");
        await page.GotoAsync(destination, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = Math.Min(20_000, Math.Max(5_000, workflow.LoginWaitSeconds * 1000))
        }).WaitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        RequireAutomationUrl(page.Url);
        return true;
    }

    private async Task<bool> TryOpenLoginDialogAsync(
        WebsiteWorkflow workflow, IPage page, CancellationToken ct)
    {
        var controls = page.Locator("button[aria-haspopup='dialog'], button[aria-controls]");
        var count = Math.Min(await controls.CountAsync().ConfigureAwait(false), 64);
        var candidates = new List<IElementHandle>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var button = controls.Nth(i);
                if (!await button.IsVisibleAsync().ConfigureAwait(false) ||
                    !await button.IsEnabledAsync().ConfigureAwait(false)) continue;
                var eligible = await button.EvaluateAsync<bool>("""
                    el => {
                      const text = (el.getAttribute('aria-label') || el.innerText || '').trim().toLowerCase()
                        .replace(/\\s+/g, ' ');
                      if (!['sign in', 'sign-in', 'log in', 'login', 'aanmelden', 'inloggen'].includes(text) ||
                          el.form || !el.isConnected || el.ownerDocument.defaultView?.document !== el.ownerDocument)
                        return false;
                      if (el.getAttribute('aria-haspopup') === 'dialog') return true;
                      const id = el.getAttribute('aria-controls');
                      const controlled = id && el.ownerDocument.getElementById(id);
                      return !!controlled && controlled.matches('dialog, [role=dialog]');
                    }
                    """).ConfigureAwait(false);
                if (!eligible) continue;
                var element = await button.ElementHandleAsync().ConfigureAwait(false);
                if (element is not null) candidates.Add(element);
                if (candidates.Count > 1) break;
            }
            if (candidates.Count != 1) return false;
            var elementToClick = candidates[0];
            if (!await elementToClick.EvaluateAsync<bool>(
                "(el, origin) => el.isConnected && el.ownerDocument.defaultView?.document === el.ownerDocument && el.ownerDocument.location.origin === origin",
                GetOrigin(page.Url)).ConfigureAwait(false)) return false;
            ct.ThrowIfCancellationRequested();
            RequireAutomationUrl(page.Url);
            Log($"Opening sign-in dialog for '{workflow.Name}'…");
            await elementToClick.ClickAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            RequireAutomationUrl(page.Url);
            return true;
        }
        finally
        {
            foreach (var element in candidates)
                await element.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<bool> HasSupportedResumedPasswordStageAsync(
        WebsiteWorkflow workflow, IPage page, CancellationToken ct)
    {
        // A password-only resumed stage is allowed only when it has the
        // semantics of a current-password login, not a reset/new-password form.
        var fields = page.Locator(workflow.PasswordSelector);
        var count = Math.Min(await fields.CountAsync().ConfigureAwait(false), 16);
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var field = fields.Nth(i);
            if (!await field.IsVisibleAsync().ConfigureAwait(false) ||
                !await field.IsEditableAsync().ConfigureAwait(false)) continue;
            var valid = await field.EvaluateAsync<bool>("""
                field => {
                  if (!(field instanceof HTMLInputElement) ||
                      field.type !== 'password' ||
                      /new-password|one-time-code/i.test(field.autocomplete || '')) return false;
                  const form = field.form;
                  if (!form) return false;
                  if (/(?:sign[ -]?up|register|create.?account|forgot|reset|change.?password)/i.test(
                      [form.id, form.getAttribute('action'), form.getAttribute('aria-label')].join(' ')))
                    return false;
                  const otherPassword = Array.from(form.elements).filter(el =>
                    el instanceof HTMLInputElement && el.type === 'password');
                  if (otherPassword.length !== 1) return false;
                  const hasFinal = Array.from(form.elements).some(el =>
                    (el instanceof HTMLButtonElement || el instanceof HTMLInputElement) &&
                    (el.type === 'submit' || /^(log in|login|sign in|signin)$/i.test(
                      (el.getAttribute('aria-label') || el.innerText || el.value || '').trim())));
                  if (!hasFinal) return false;
                  const name = [form.id, form.getAttribute('aria-label'), form.getAttribute('action')].join(' ');
                  const path = field.ownerDocument.location.pathname;
                  return /current-password/i.test(field.autocomplete || '') ||
                    /(?:login|log-in|signin|sign-in|password|auth)/i.test(name + ' ' + path);
                }
                """).ConfigureAwait(false);
            if (valid) return true;
        }
        return false;
    }

    private static bool IsLocalSignInLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        var name = Regex.Replace(label.Trim(), @"\s+", " ").ToLowerInvariant();
        return name is "sign in" or "sign-in" or "log in" or "login" or "aanmelden" or "inloggen";
    }

    private static async Task<bool> HasOwnedIdentifierStageAsync(
        WebsiteWorkflow workflow, IPage page, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(workflow.UsernameContinueSelector) &&
            await LoginFieldReadyAsync(page, workflow.UsernameSelector, ct).ConfigureAwait(false))
            return true;
        if (workflow.UsernameSelector != StandardUsernameSelector)
        {
            // A deliberate configured selector can be a no-form SPA.
            return await LoginFieldReadyAsync(page, workflow.UsernameSelector, ct).ConfigureAwait(false);
        }
        await using var candidate = await ResolveAutomaticUsernameAsync(page, ct).ConfigureAwait(false);
        return candidate is not null;
    }

    // The automatic default never chooses the first generic email box. Only
    // an unambiguous identifier belonging to an actual login form is admitted.
    // The returned handle is passed to the existing protected fill boundary.
    private static async Task<IElementHandle?> ResolveAutomaticUsernameAsync(IPage page, CancellationToken ct)
    {
        var fields = page.Locator(AutomaticUsernameCandidates);
        var count = Math.Min(await fields.CountAsync().ConfigureAwait(false), 32);
        IElementHandle? selected = null;
        var highest = 0;
        var tied = false;
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var field = fields.Nth(i);
            if (!await field.IsVisibleAsync().ConfigureAwait(false) ||
                !await field.IsEditableAsync().ConfigureAwait(false)) continue;
            var element = await field.ElementHandleAsync().ConfigureAwait(false);
            if (element is null) continue;
            try
            {
                var score = await element.EvaluateAsync<int>("""
                    field => {
                      const form = field.form;
                      if (!form || !field.isConnected || field.ownerDocument.defaultView?.document !== field.ownerDocument)
                        return 0;
                      const forbidden = /(?:sign[ -]?up|register|create.?account|forgot|reset|change.?password)/i;
                      const purpose = [form.id, form.getAttribute('action'), form.getAttribute('aria-label')].join(' ');
                      if (forbidden.test(purpose)) return 0;
                      const controls = Array.from(form.elements);
                      const passwords = controls.filter(el => el instanceof HTMLInputElement &&
                        el.type === 'password' && !/new-password|one-time-code/.test(el.autocomplete || ''));
                      if (passwords.length > 1) return 0;
                      const signInActions = controls.filter(el => {
                        if (!(el instanceof HTMLButtonElement || el instanceof HTMLInputElement)) return false;
                        if (el.disabled || el.getClientRects().length === 0) return false;
                        const label = (el.getAttribute('aria-label') || el.innerText || el.value || '').trim();
                        return /^(log in|login|sign in|signin)$/i.test(label);
                      });
                      if (passwords.length === 1)
                        return signInActions.length === 1 || /(?:login|log-in|signin|sign-in|auth)/i.test(purpose) ? 3 : 0;
                      const continuations = controls.filter(el => {
                        if (!(el instanceof HTMLButtonElement || el instanceof HTMLInputElement)) return false;
                        if (el.disabled || el.getClientRects().length === 0) return false;
                        const label = (el.getAttribute('aria-label') || el.innerText || el.value || '').trim().toLowerCase();
                        return /^(next|continue|continue to password|log in|login|sign in|signin)$/i.test(label);
                      });
                      return continuations.length === 1 ? 2 : 0;
                    }
                    """).ConfigureAwait(false);
                if (score > highest)
                {
                    if (selected is not null) await selected.DisposeAsync().ConfigureAwait(false);
                    selected = element;
                    element = null;
                    highest = score;
                    tied = false;
                }
                else if (score > 0 && score == highest) tied = true;
            }
            finally
            {
                if (element is not null) await element.DisposeAsync().ConfigureAwait(false);
            }
        }
        if (tied)
        {
            if (selected is not null) await selected.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        return selected;
    }
}
