using System.IO;
using LaunchFlow.Models;
using Microsoft.Playwright;

namespace LaunchFlow.Services;

/// <summary>Browser login and credential-bound workflow execution; kept in the
/// AutomationEngine partial type to preserve existing session ownership and policy guards.
/// </summary>
public sealed partial class AutomationEngine
{
    private async Task<LoginAttention?> RunOneAsync(
        WebsiteWorkflow workflow,
        CancellationToken ct,
        Configuration? configuration,
        string? keepassMasterPassword)
    {
        ct.ThrowIfCancellationRequested();
        Log($"Starting '{workflow.Name}'…");
        ValidateUrl(workflow.StartUrl);
        RequireAutomationUrl(workflow.StartUrl);
        if (!string.IsNullOrWhiteSpace(workflow.DestinationUrl))
        {
            ValidateUrl(workflow.DestinationUrl);
            RequireAutomationUrl(workflow.DestinationUrl);
        }

        if (IsDefaultBrowserOpenOnly(workflow.Browser))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = workflow.StartUrl,
                UseShellExecute = true
            });
            Log($"Opened '{workflow.Name}' with the default browser (open only).");
            return null;
        }

        var page = await GetOrCreatePageAsync(workflow, ct).ConfigureAwait(false);
        await PositionWindowAsync(page, workflow).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var currentUrl = page.Url;
        var alreadyOnSite = !string.IsNullOrEmpty(currentUrl) &&
                            Uri.TryCreate(currentUrl, UriKind.Absolute, out var cur) &&
                            Uri.TryCreate(workflow.StartUrl, UriKind.Absolute, out var start) &&
                            string.Equals(cur.Scheme, start.Scheme, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(cur.Authority, start.Authority, StringComparison.OrdinalIgnoreCase);

        if (!alreadyOnSite)
        {
            RequireAutomationUrl(workflow.StartUrl);
            await page.GotoAsync(workflow.StartUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            }).ConfigureAwait(false);
        }
        else
        {
            Log($"Reusing open page for '{workflow.Name}'.");
        }

        ct.ThrowIfCancellationRequested();
        RequireAutomationUrl(page.Url);
        if (workflow.AutoLogin &&
            !string.Equals(workflow.LoginMode, "None", StringComparison.OrdinalIgnoreCase))
        {
            var manualConfirmation = await ManuallyConfirmedAsync(workflow, page, ct).ConfigureAwait(false);
            var loginCompleted = manualConfirmation ||
                string.Equals(workflow.LoginMode, "SSO", StringComparison.OrdinalIgnoreCase) ||
                await TryAutoLoginAsync(workflow, page, ct, configuration, keepassMasterPassword)
                    .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            RequireAutomationUrl(page.Url);
            if (!loginCompleted)
                return new LoginAttention(workflow.Id, workflow.Name,
                    "Auto-login was interrupted or its credential field was not approved/ready. Retry sign-in manually before continuing.");
            var health = manualConfirmation
                ? new LoginHealth.Report(false, "User explicitly confirmed sign-in for the current page.")
                : await LoginHealth.InspectAsync(page, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            RequireAutomationUrl(page.Url);
            if (health.NeedsAttention)
            {
                Log($"Login needs attention for '{workflow.Name}': {health.Reason} Pausing further steps for this site.");
                return new LoginAttention(workflow.Id, workflow.Name, health.Reason);
            }
            if (!manualConfirmation &&
                (workflow.Steps.Count > 0 || !string.IsNullOrWhiteSpace(workflow.DestinationUrl)))
            {
                var verified = await LoginHealth.VerifySuccessAsync(
                    page, workflow.LoginSuccessSelector, workflow.LoginSuccessUrlContains, ct)
                    .ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                RequireAutomationUrl(page.Url);
                if (verified.NeedsAttention)
                {
                    Log($"Login not verified for '{workflow.Name}'. Follow-up actions were paused.");
                    return new LoginAttention(workflow.Id, workflow.Name, verified.Reason);
                }
            }
        }

        foreach (var step in workflow.Steps)
        {
            ct.ThrowIfCancellationRequested();
            // A failed action leaves browser state uncertain; do not dispatch later steps or a destination navigation.
            try { await ExecuteStepAsync(page, step, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw new WorkflowStepException(PublicStepType(step.Type)); }
            ct.ThrowIfCancellationRequested();
        }

        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(workflow.DestinationUrl))
        {
            RequireAutomationUrl(page.Url);
            RequireAutomationUrl(workflow.DestinationUrl);
            await page.GotoAsync(workflow.DestinationUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            }).ConfigureAwait(false);
        }

        RequireAutomationUrl(page.Url);
        Log($"Finished '{workflow.Name}'.");
        return null;
    }

    private async Task<bool> TryAutoLoginAsync(
        WebsiteWorkflow workflow,
        IPage page,
        CancellationToken ct,
        Configuration? configuration,
        string? keepassMasterPassword)
    {
        ct.ThrowIfCancellationRequested();
        RequireCredentialAccess();
        // Reused persistent browser sessions may already be authenticated.
        // Honor an explicitly configured success element only when no login
        // input is visible. Never skip a visible password challenge.
        if (!string.IsNullOrWhiteSpace(workflow.LoginSuccessSelector) &&
            !string.Equals(workflow.LoginSuccessSelector.Trim(), "body", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(workflow.LoginSuccessSelector.Trim(), "html", StringComparison.OrdinalIgnoreCase) &&
            await page.Locator(workflow.LoginSuccessSelector).First.IsVisibleAsync().ConfigureAwait(false) &&
            (string.IsNullOrWhiteSpace(workflow.UsernameSelector) ||
                !await page.Locator(workflow.UsernameSelector).First.IsVisibleAsync().ConfigureAwait(false)) &&
            (string.IsNullOrWhiteSpace(workflow.PasswordSelector) ||
                !await page.Locator(workflow.PasswordSelector).First.IsVisibleAsync().ConfigureAwait(false)))
        {
            Log($"Already signed in for '{workflow.Name}'. Reusing the existing browser session.");
            return true;
        }
        // Repeated Run / Windows-unlock should not attempt a second password
        // insertion into the existing dashboard when the configured Start URL
        // is a login path and that same-origin signed-in redirect is still live.
        if (await LoginHealth.VerifiedRedirectAsync(page, workflow.StartUrl, ct).ConfigureAwait(false))
        {
            RequireCredentialAccess();
            RequireAutomationUrl(page.Url);
            Log($"Existing signed-in dashboard confirmed for '{workflow.Name}'.");
            return true;
        }
        var credential = _vault.Read(workflow, configuration, keepassMasterPassword);
        if (credential is null)
        {
            Log($"No saved credential for '{workflow.Name}'. Leaving the page open so you can sign in manually.");
            return false;
        }

        string? submitPageBeforeClick = null;
        var initialLoginUrl = page.Url;
        var submitWasAttempted = false;
        try
        {
            var requiresUser = !string.IsNullOrWhiteSpace(workflow.UsernameSelector);
            var requiresPassword = !string.IsNullOrWhiteSpace(workflow.PasswordSelector);
            if ((!requiresUser && !requiresPassword) ||
                (requiresUser && string.IsNullOrWhiteSpace(credential.Username)) ||
                (requiresPassword && string.IsNullOrEmpty(credential.Password)))
            {
                Log($"Missing required saved sign-in details for '{workflow.Name}'. Sign in manually or update credentials.");
                return false;
            }

            string? approvedSubmitOrigin = null;
            if (!string.IsNullOrWhiteSpace(workflow.UsernameSelector))
            {
                var usernameOrigin = await FillCredentialAsync(workflow, page, workflow.UsernameSelector,
                    credential.Username, ct).ConfigureAwait(false);
                if (usernameOrigin is not null)
                {
                    approvedSubmitOrigin = usernameOrigin;
                    if (!string.IsNullOrWhiteSpace(workflow.UsernameContinueSelector))
                    {
                        await ExecuteStepAsync(page, new WorkflowStep
                        {
                            Type = "Click", Target = workflow.UsernameContinueSelector
                        }, ct).ConfigureAwait(false);
                        await Task.Delay(800, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    Log($"Username field not found for '{workflow.Name}'. Check the selector or sign in manually.");
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(workflow.PasswordSelector))
            {
                var passwordOrigin = await FillCredentialAsync(workflow, page, workflow.PasswordSelector,
                    credential.Password, ct).ConfigureAwait(false);
                if (passwordOrigin is not null)
                {
                    approvedSubmitOrigin = passwordOrigin;
                }
                else
                {
                    Log($"Password field not found for '{workflow.Name}'. Check the selector or sign in manually.");
                    return false;
                }
            }

            // A partial login must never press Submit. Select a visible, enabled
            // button instead of the hidden template many JS applications render first.
            if (!string.IsNullOrWhiteSpace(workflow.SubmitSelector))
            {
                submitPageBeforeClick = page.Url;
                submitWasAttempted = true;
                if (!await ClickLoginSubmitAsync(workflow, page, approvedSubmitOrigin, ct).ConfigureAwait(false))
                {
                    // Some JavaScript login forms submit automatically after
                    // the final input event. Admit only a verified redirect;
                    // a missing button on the login page is still an error.
                    if (await ObserveVerifiedRedirectAsync(page, initialLoginUrl, 1500, ct).ConfigureAwait(false))
                    {
                        RequireCredentialAccess();
                        RequireAutomationUrl(page.Url);
                        Log($"Signed-in dashboard confirmed for '{workflow.Name}'.");
                        return true;
                    }
                    Log($"Login submit button not ready for '{workflow.Name}'. Check its selector.");
                    return false;
                }
            }

            ct.ThrowIfCancellationRequested();
            var waitMs = Math.Clamp(workflow.LoginWaitSeconds, 5, 180) * 1000;
            if (!string.IsNullOrWhiteSpace(workflow.LoginSuccessSelector) ||
                !string.IsNullOrWhiteSpace(workflow.LoginSuccessUrlContains))
            {
                if (!await ObserveLoginCompletionAsync(workflow, page,
                    submitPageBeforeClick, waitMs, ct).ConfigureAwait(false))
                {
                    Log($"Sign-in for '{workflow.Name}' was not verified. Check or repair the completion condition.");
                    return false;
                }
            }
            else
            {
                // If no explicit success condition was taught, keep the prior
                // conservative challenge inspection in RunOneAsync.
                await Task.Delay(Math.Min(waitMs, 3000), ct).ConfigureAwait(false);
            }

            Log($"Auto-login attempted for '{workflow.Name}'.");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Playwright may report that a submitted button detached during
            // successful navigation. Admit only a *verified* same-origin signed-in
            // redirect, never an arbitrary failed/blocked/MFA page.
            if (submitWasAttempted && submitPageBeforeClick is not null &&
                await ObserveVerifiedRedirectAsync(page, submitPageBeforeClick, 2000, ct).ConfigureAwait(false))
            {
                try
                {
                    RequireCredentialAccess();
                    RequireAutomationUrl(page.Url);
                    Log($"Signed-in dashboard confirmed for '{workflow.Name}' after navigation.");
                    return true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { /* revoked policy never authorizes success */ }
            }
            Log($"Auto-login for '{workflow.Name}' incomplete. " +
                "Leaving the browser open for manual sign-in / MFA.");
            return false;
        }
    }

    private static async Task<bool> ObserveVerifiedRedirectAsync(
        IPage page, string startingUrl, int maxWaitMs, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(maxWaitMs);
        do
        {
            ct.ThrowIfCancellationRequested();
            if (await LoginHealth.VerifiedRedirectAsync(page, startingUrl, ct).ConfigureAwait(false))
                return true;
            if (DateTimeOffset.UtcNow >= deadline) return false;
            await Task.Delay(200, ct).ConfigureAwait(false);
        } while (true);
    }

    private static async Task<bool> ObserveLoginCompletionAsync(
        WebsiteWorkflow workflow, IPage page, string? submittedFrom, int waitMs, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(waitMs);
        do
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                bool selectorReady = string.IsNullOrWhiteSpace(workflow.LoginSuccessSelector) ||
                    await page.Locator(workflow.LoginSuccessSelector).First.IsVisibleAsync().ConfigureAwait(false);
                bool urlReady = string.IsNullOrWhiteSpace(workflow.LoginSuccessUrlContains) ||
                    page.Url.Contains(workflow.LoginSuccessUrlContains, StringComparison.OrdinalIgnoreCase);
                if (selectorReady && urlReady) return true;
                if (submittedFrom is not null &&
                    await LoginHealth.VerifiedRedirectAsync(page, submittedFrom, ct).ConfigureAwait(false))
                    return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { /* page may be mid-navigation */ }
            if (DateTimeOffset.UtcNow >= deadline) return false;
            await Task.Delay(250, ct).ConfigureAwait(false);
        } while (true);
    }

    private async Task<IElementHandle?> ResolveCredentialElementAsync(
        WebsiteWorkflow workflow, IPage page, string selector, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        async Task<IElementHandle?> VisibleFirstAsync()
        {
            var locator = page.Locator(selector);
            var count = await locator.CountAsync().ConfigureAwait(false);
            for (var i = 0; i < count; i++)
            {
                var candidate = locator.Nth(i);
                if (await candidate.IsVisibleAsync().ConfigureAwait(false) &&
                    await candidate.IsEnabledAsync().ConfigureAwait(false))
                    return await candidate.ElementHandleAsync().ConfigureAwait(false);
            }
            // Preserve the original document-bound readiness wait for a field
            // that exists but has not become editable yet.
            return count > 0 ? await locator.First.ElementHandleAsync().ConfigureAwait(false) : null;
        }

        // Preserve the original one-time QuerySelector handle for immediately
        // available fields. It is also the policy/cancellation enforcement seam
        // tested for post-fill revocation. Never replace this handle before fill.
        var first = await page.QuerySelectorAsync(selector).ConfigureAwait(false);
        if (first is not null)
        {
            if (await first.IsVisibleAsync().ConfigureAwait(false) &&
                await first.IsEnabledAsync().ConfigureAwait(false)) return first;
            var alternate = await VisibleFirstAsync().ConfigureAwait(false);
            if (alternate is not null)
            {
                await first.DisposeAsync().ConfigureAwait(false);
                return alternate;
            }
            return first;
        }

        // Locator waits may outlive a cancellation, but never carry a password.
        // Validate the original document identity before returning any handle.
        await using var originalDocument = await page.EvaluateHandleAsync("() => document").ConfigureAwait(false);
        try
        {
            await page.Locator(selector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = Math.Min(10_000, Math.Max(2_000, workflow.LoginWaitSeconds * 1000))
            }).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var element = await VisibleFirstAsync().ConfigureAwait(false);
        if (element is null) return null;
        try
        {
            if (!await element.EvaluateAsync<bool>(
                "(element, original) => element.isConnected && element.ownerDocument === original",
                originalDocument).ConfigureAwait(false))
                throw new InvalidOperationException("Sign-in document changed while the form was loading.");
            return element;
        }
        catch
        {
            await element.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<bool> ClickLoginSubmitAsync(
        WebsiteWorkflow workflow, IPage page, string? approvedCredentialOrigin, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var locator = page.Locator(workflow.SubmitSelector);
        var count = await locator.CountAsync().ConfigureAwait(false);
        for (var i = 0; i < count; i++)
        {
            var item = locator.Nth(i);
            if (!await item.IsVisibleAsync().ConfigureAwait(false) ||
                !await item.IsEnabledAsync().ConfigureAwait(false)) continue;
            await using var element = await item.ElementHandleAsync().ConfigureAwait(false);
            if (element is null) continue;
            var origin = await element.EvaluateAsync<string>("""
                element => {
                    const doc = element.ownerDocument;
                    if (!element.isConnected || doc.defaultView?.document !== doc)
                        throw new Error('Login button document changed');
                    return doc.location.origin;
                }
                """).ConfigureAwait(false);
            RequireCredentialAccess();
            RequireAutomationUrl(origin);
            // Password approval already happened in FillCredentialAsync. Avoid
            // re-prompting for the same SSO origin, but never click a form on a
            // different site than the one that received the saved credential.
            if (string.IsNullOrWhiteSpace(approvedCredentialOrigin) ||
                !OriginsMatch(origin, approvedCredentialOrigin)) return false;
            if (!await element.EvaluateAsync<bool>(
                "(el, approved) => el.isConnected && el.ownerDocument.defaultView?.document === el.ownerDocument && el.ownerDocument.location.origin === approved",
                origin).ConfigureAwait(false)) return false;
            ct.ThrowIfCancellationRequested();
            await element.ClickAsync().ConfigureAwait(false);
            RequireAutomationUrl(page.Url);
            return true;
        }
        return false;
    }

    private async Task<string?> FillCredentialAsync(
        WebsiteWorkflow workflow, IPage page, string selector, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Bind a concrete field before inserting a secret. Wait for JS-rendered
        // forms, but never allow that wait to retarget across a new document.
        await using var element = await ResolveCredentialElementAsync(workflow, page, selector, ct).ConfigureAwait(false);
        if (element is null) return null;
        var frame = await element.OwnerFrameAsync().ConfigureAwait(false);
        if (frame is null) throw new InvalidOperationException("Credential field has no owning frame. Retry sign-in manually.");
        var navigated = 0;
        void Invalidate(object? sender, IFrame changed)
        {
            if (ReferenceEquals(changed, frame)) Interlocked.Exchange(ref navigated, 1);
        }
        page.FrameNavigated += Invalidate;
        page.FrameDetached += Invalidate;
        try
        {
            await using var document = await element.EvaluateHandleAsync("element => element.ownerDocument").ConfigureAwait(false);
            async Task<string> VerifyAsync()
            {
                ct.ThrowIfCancellationRequested();
                if (Volatile.Read(ref navigated) != 0 || frame.IsDetached ||
                    !ReferenceEquals(await element.OwnerFrameAsync().ConfigureAwait(false), frame))
                    throw new InvalidOperationException("Credential document changed. Retry sign-in manually.");
                var origin = await element.EvaluateAsync<string>("""
                    (element, document) => {
                        if (!element.isConnected || element.ownerDocument !== document ||
                            document.defaultView?.document !== document)
                            throw new Error('Credential document changed');
                        return document.location.origin;
                    }
                    """, document).ConfigureAwait(false);
                ValidateUrl(origin);
                RequireCredentialAccess();
                RequireAutomationUrl(origin);
                if (new Uri(origin).Scheme != Uri.UriSchemeHttps)
                    throw new InvalidOperationException("Automatic credentials require HTTPS. Use manual sign-in for HTTP sites.");
                if (Volatile.Read(ref navigated) != 0)
                    throw new InvalidOperationException("Credential document changed. Retry sign-in manually.");
                return origin;
            }

            var origin = await VerifyAsync().ConfigureAwait(false);
            await element.WaitForElementStateAsync(ElementState.Visible).WaitAsync(ct).ConfigureAwait(false);
            await element.WaitForElementStateAsync(ElementState.Editable).WaitAsync(ct).ConfigureAwait(false);
            if (!OriginsMatch(origin, await VerifyAsync().ConfigureAwait(false)))
                throw new InvalidOperationException("Credential origin changed. Retry sign-in manually.");
            if (!await IsOriginAllowedAsync(workflow, origin).WaitAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("Credential origin was not approved. Complete sign-in manually.");
            // Approval can await a dialog while the page navigates or the bound field becomes unavailable.
            await element.WaitForElementStateAsync(ElementState.Visible).WaitAsync(ct).ConfigureAwait(false);
            await element.WaitForElementStateAsync(ElementState.Editable).WaitAsync(ct).ConfigureAwait(false);
            if (!OriginsMatch(origin, await VerifyAsync().ConfigureAwait(false)))
                throw new InvalidOperationException("Credential origin changed. Retry sign-in manually.");
            ct.ThrowIfCancellationRequested();
            await element.FillAsync(value).ConfigureAwait(false);
            return origin;
        }
        catch (PlaywrightException)
        {
            // Playwright fill diagnostics can contain the value; only a non-secret retry reason may leave this boundary.
            throw new InvalidOperationException("Credential field changed or was not ready. Retry sign-in manually.");
        }
        finally
        {
            page.FrameNavigated -= Invalidate;
            page.FrameDetached -= Invalidate;
        }
    }

    private async Task<bool> IsOriginAllowedAsync(WebsiteWorkflow workflow, string origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return false;

        if (!string.IsNullOrWhiteSpace(workflow.CredentialOrigin) &&
            OriginsMatch(origin, GetOrigin(workflow.CredentialOrigin)))
            return true;

        if (!string.IsNullOrWhiteSpace(workflow.PasswordCredentialOrigin) &&
            OriginsMatch(origin, GetOrigin(workflow.PasswordCredentialOrigin)))
            return true;

        if (workflow.ApprovedLoginOrigins.Contains(origin))
            return true;

        // First visit of start URL host is implicitly trusted for the configured start site.
        if (Uri.TryCreate(workflow.StartUrl, UriKind.Absolute, out var start) &&
            OriginsMatch(origin, $"{start.Scheme}://{start.Authority}"))
        {
            workflow.ApprovedLoginOrigins.Add(origin);
            return true;
        }

        if (ApproveLoginOriginAsync is null) return false;
        return await ApproveLoginOriginAsync(workflow, origin).ConfigureAwait(false);
    }

}
