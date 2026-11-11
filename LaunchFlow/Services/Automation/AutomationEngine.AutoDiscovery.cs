using System.Text.RegularExpressions;
using LaunchFlow.Models;
using Microsoft.Playwright;

namespace LaunchFlow.Services;

public sealed partial class AutomationEngine
{
    private const string StandardUsernameSelector = LoginFieldDiscovery.StandardUsernameSelector;
    private const string StandardSubmitSelector = LoginFieldDefaults.SubmitSelector;
    // Built-in password discovery is limited to actual password inputs. A
    // legacy CSS default also matches #secret/#password on non-input elements,
    // such as <form id="secret">, and must never make a password stage ambiguous.
    // Deliberate custom selectors retain their exact user-specified semantics.
    private static string PasswordCandidateSelector(WebsiteWorkflow workflow) =>
        LoginFieldDiscovery.UsesAutomaticPasswordDiscovery(workflow)
            ? "input[type='password']"
            : workflow.PasswordSelector;
    private const string AutomaticUsernameCandidates =
        "input[autocomplete~='username' i], input[name='login' i], input[name='identity' i], #login_field, " +
        "input[type='email'], input[name*='user' i], input[name*='email' i], input[name*='identity' i], " +
        "#username, #email, #identity";

    // Discover only existing same-origin HTTPS links. No guessed /login paths,
    // SSO buttons, cross-origin redirects or free-form page-wide click search.
    private async Task<bool> TryOpenLoginEntryAsync(
        WebsiteWorkflow workflow, IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var current) ||
            current.Scheme != Uri.UriSchemeHttps) return false;
        if (LoginFieldDiscovery.UsesAutomaticUsernameDiscovery(workflow)
                ? await HasSupportedResumedPasswordStageAsync(workflow, page, ct).ConfigureAwait(false)
                : await PasswordStageReadyAsync(workflow, page, ct).ConfigureAwait(false))
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
                        .replace(/\s+/g, ' ');
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
        await using var matched = await ResolveSupportedResumedPasswordStageAsync(
            workflow, page, ct).ConfigureAwait(false);
        return matched is not null;
    }

    private static async Task<IElementHandle?> ResolveSupportedResumedPasswordStageAsync(
        WebsiteWorkflow workflow, IPage page, CancellationToken ct)
    {
        // A password-only resumed stage is allowed only when it has the
        // semantics of a current-password login, not a reset/new-password form.
        var fields = page.Locator(PasswordCandidateSelector(workflow));
        var count = Math.Min(await fields.CountAsync().ConfigureAwait(false), 16);
        IElementHandle? selected = null;
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
                  const name = [form.id, form.getAttribute('aria-label'), form.getAttribute('action')].join(' ');
                  const path = field.ownerDocument.location.pathname;
                  return /current-password/i.test(field.autocomplete || '') ||
                    /(?:login|log-in|signin|sign-in|password|auth)/i.test(name + ' ' + path);
                }
                """).ConfigureAwait(false);
            if (!valid) continue;
            var element = await field.ElementHandleAsync().ConfigureAwait(false);
            if (element is null) continue;
            if (!await HasOwnedStageActionAsync(workflow, page, element, ct).ConfigureAwait(false))
            {
                await element.DisposeAsync().ConfigureAwait(false);
                continue;
            }
            if (selected is not null)
            {
                await element.DisposeAsync().ConfigureAwait(false);
                await selected.DisposeAsync().ConfigureAwait(false);
                return null;
            }
            selected = element;
        }
        return selected;
    }

    private async Task<bool> HasConfiguredResumedPasswordStageAsync(
        WebsiteWorkflow workflow, IPage page, CancellationToken ct)
    {
        if (!await PasswordStagePresentAsync(workflow, page, ct).ConfigureAwait(false)) return false;
        await using var selected = await ResolveConfiguredPasswordStageAsync(workflow, page, ct)
            .ConfigureAwait(false);
        return selected is not null;
    }

    // Automatic classification stays strict. A deliberate custom selector can
    // select the sole actionable password in a known login form while ignoring
    // hidden template copies; it never searches for a global first match.
    private static async Task<(bool Present, IElementHandle? Field)> ResolveCombinedPasswordAsync(
        WebsiteWorkflow workflow, IPage page, CredentialFieldBinding usernameOwner,
        bool discoverPassword, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var status = await usernameOwner.Field.EvaluateAsync<int>("""
            (field, form) => {
              if (!field.isConnected || !form?.isConnected || field.form !== form ||
                  field.ownerDocument.defaultView?.document !== field.ownerDocument) return -1;
              const passwords = Array.from(form.elements).filter(el =>
                  el instanceof HTMLInputElement && el.type === 'password');
              if (passwords.length === 0) return 0;
              if (passwords.some(password =>
                  /new-password|one-time-code/i.test(password.autocomplete || ''))) return -1;
              const intent = [form.id, form.getAttribute('action'), form.getAttribute('aria-label')].join(' ');
              if (/(?:sign[ -]?up|register|create.?account|forgot|reset|change.?password)/i.test(intent))
                return -1;
              return passwords.length === 1 ? 1 : 2;
            }
            """, usernameOwner.Form).ConfigureAwait(false);
        if (status < 0) return (true, null);
        if (status == 0) return (false, null);
        if (discoverPassword && status != 1) return (true, null);

        var matches = page.Locator(PasswordCandidateSelector(workflow));
        var count = await matches.CountAsync().ConfigureAwait(false);
        if (count > 32) return (true, null);
        var owned = new List<IElementHandle>();
        var visible = new List<IElementHandle>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = await matches.Nth(i).ElementHandleAsync().ConfigureAwait(false);
                if (candidate is null) continue;
                var belongs = await candidate.EvaluateAsync<bool>("""
                    (field, form) => field.isConnected && field.form === form &&
                        field.ownerDocument.defaultView?.document === field.ownerDocument &&
                        field instanceof HTMLInputElement && field.type === 'password' &&
                        !/new-password|one-time-code/i.test(field.autocomplete || '')
                    """, usernameOwner.Form).ConfigureAwait(false);
                if (!belongs)
                {
                    await candidate.DisposeAsync().ConfigureAwait(false);
                    continue;
                }
                owned.Add(candidate);
                if (await candidate.IsVisibleAsync().ConfigureAwait(false))
                    visible.Add(candidate);
            }
            // Zero matches in the proven form means the password stage has not
            // arrived; a password in some other form cannot authorize a fill.
            if (owned.Count == 0) return (false, null);
            IElementHandle? selected = visible.Count == 1
                ? visible[0]
                : visible.Count == 0 && owned.Count == 1 ? owned[0] : null;
            // Automatic selection must never accept duplicate candidates,
            // even if one is currently hidden.
            if (discoverPassword && owned.Count != 1) selected = null;
            if (selected is null) return (true, null);
            owned.Remove(selected);
            return (true, selected);
        }
        finally
        {
            foreach (var field in owned)
                await field.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Explicit selectors are not automatically classified as login forms, but
    // they still require one stable, unambiguous field. The protected fill
    // boundary owns readiness, cancellation, frame origin and authorization.
    private async Task<IElementHandle?> ResolveConfiguredPasswordStageAsync(
        WebsiteWorkflow workflow, IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Refuse an already-ambiguous selector before binding its first match.
        // Rechecking below also catches additional matches that appear while
        // a delayed field is awaited.
        var matches = page.Locator(workflow.PasswordSelector);
        if (await matches.CountAsync().ConfigureAwait(false) > 1) return null;
        var selected = await ResolveCredentialElementAsync(
            workflow, page, workflow.PasswordSelector, ct).ConfigureAwait(false);
        if (selected is null) return null;
        try
        {
            if (await matches.CountAsync().ConfigureAwait(false) != 1) return null;
            if (!await selected.EvaluateAsync<bool>("""
                field => {
                  if (!field.isConnected ||
                      field.ownerDocument.defaultView?.document !== field.ownerDocument ||
                      !(field instanceof HTMLInputElement) || field.type !== 'password' ||
                      /new-password|one-time-code/i.test(field.autocomplete || '')) return false;
                  const form = field.form;
                  if (form && /(?:sign[ -]?up|register|create.?account|forgot|reset|change.?password)/i.test(
                      [form.id, form.getAttribute('action'), form.getAttribute('aria-label')].join(' ')))
                    return false;
                  return true;
                }
                """).ConfigureAwait(false)) return null;
            var result = selected;
            selected = null;
            return result;
        }
        finally
        {
            if (selected is not null) await selected.DisposeAsync().ConfigureAwait(false);
        }
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
        if (!LoginFieldDiscovery.UsesAutomaticUsernameDiscovery(workflow))
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
                      if (!field.isConnected || field.ownerDocument.defaultView?.document !== field.ownerDocument)
                        return 0;
                      const forbidden = /(?:sign[ -]?up|register|create.?account|forgot|reset|change.?password)/i;
                      const form = field.form;
                      const path = field.ownerDocument.location.pathname;
                      const loginPath = /\/(?:login|log-in|signin|sign-in|auth)(?:\/|$)/i.test(path);
                      // React/Formik pages (modern npm) often have no native <form>.
                      // Scope to the nearest card/section, or the document, instead.
                      const root = form || field.closest('form, section, main, [role="main"], .login, #login, .card') ||
                        field.ownerDocument;
                      const purpose = form
                        ? [form.id, form.getAttribute('action'), form.getAttribute('aria-label')].join(' ')
                        : [root.id || '', root.getAttribute?.('aria-label') || '', path].join(' ');
                      if (forbidden.test(purpose)) return 0;
                      const controls = form
                        ? Array.from(form.elements)
                        : Array.from(root.querySelectorAll('input, button, [role="button"]'));
                      const passwords = controls.filter(el => el instanceof HTMLInputElement &&
                        el.type === 'password' && el.getClientRects().length > 0);
                      if (passwords.some(el => /new-password|one-time-code/i.test(el.autocomplete || '')) ||
                          passwords.length > 1) return 0;
                      const signInActions = controls.filter(el => {
                        if (!(el instanceof HTMLButtonElement || el instanceof HTMLInputElement ||
                              el.getAttribute('role') === 'button')) return false;
                        if (el.getClientRects().length === 0) return false;
                        const label = (el.getAttribute('aria-label') || el.innerText || el.value || '').trim();
                        return /^(log in|login|sign in|signin)$/i.test(label);
                      });
                      if (passwords.length === 1)
                        return signInActions.length === 1 ||
                          /(?:login|log-in|signin|sign-in|auth|identity)/i.test(purpose) ||
                          (loginPath && signInActions.length >= 1) ? 3 : 0;
                      const continuations = controls.filter(el => {
                        if (!(el instanceof HTMLButtonElement || el instanceof HTMLInputElement ||
                              el.getAttribute('role') === 'button')) return false;
                        if (el.getClientRects().length === 0) return false;
                        const label = (el.getAttribute('aria-label') || el.innerText || el.value || '').trim().toLowerCase();
                        return /^(next|continue|continue to password|log in|login|sign in|signin)$/i.test(label);
                      });
                      if (continuations.length !== 1) return 0;
                      // Email + Continue is not login intent. Require an
                      // authentication-specific form, identifier, or URL.
                      const explicitIntent = /(?:login|log-in|signin|sign-in|auth|identifier|identity)/i.test(purpose) ||
                        /(?:^|\s)username(?:\s|$)/i.test(field.autocomplete || '') ||
                        /^(?:login|login_field|username|identity)$/i.test(field.id || '') ||
                        /^(?:login|username|identity)$/i.test(field.name || '');
                      if (explicitIntent) return 2;
                      const label = (continuations[0].getAttribute('aria-label') ||
                        continuations[0].innerText || continuations[0].value || '').trim();
                      return loginPath &&
                        /^(?:next|log in|login|sign in|signin)$/i.test(label) ? 1 : 0;
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
