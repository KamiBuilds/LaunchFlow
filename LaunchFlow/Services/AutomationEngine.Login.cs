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
                (string.Equals(workflow.LoginMode, "SSO", StringComparison.OrdinalIgnoreCase)
                    ? await VerifyBrowserLedSsoAsync(workflow, page, ct).ConfigureAwait(false)
                    : await TryAutoLoginAsync(workflow, page, ct, configuration, keepassMasterPassword)
                        .ConfigureAwait(false));
            ct.ThrowIfCancellationRequested();
            RequireAutomationUrl(page.Url);
            if (!loginCompleted)
            {
                var status = await LoginHealth.InspectAsync(page, ct).ConfigureAwait(false);
                return new LoginAttention(workflow.Id, workflow.Name,
                    status.NeedsAttention ? status.Reason :
                    "Could not verify the authenticated application or safely finish a sign-in stage.");
            }
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
            // A verified automatic saved-password attempt already established
            // completion when no technical success selector was configured.
            // Browser-led SSO still requires explicit evidence or confirmation.
            if (!manualConfirmation &&
                (workflow.Steps.Count > 0 || !string.IsNullOrWhiteSpace(workflow.DestinationUrl)) &&
                (!string.IsNullOrWhiteSpace(workflow.LoginSuccessSelector) ||
                 !string.IsNullOrWhiteSpace(workflow.LoginSuccessUrlContains)))
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

    private static async Task<bool> VerifyBrowserLedSsoAsync(
        WebsiteWorkflow workflow, IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireAutomationUrl(page.Url);
        if (await LoginHealth.VerifiedApplicationSessionAsync(page, workflow.StartUrl, ct)
            .ConfigureAwait(false)) return true;
        if (string.IsNullOrWhiteSpace(workflow.LoginSuccessSelector)) return false;
        if (!OriginsMatch(GetOrigin(page.Url), GetOrigin(workflow.StartUrl))) return false;
        if ((await LoginHealth.InspectAsync(page, ct).ConfigureAwait(false)).NeedsAttention) return false;
        var result = await LoginHealth.VerifySuccessAsync(
            page, workflow.LoginSuccessSelector, workflow.LoginSuccessUrlContains, ct)
            .ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        RequireAutomationUrl(page.Url);
        return !result.NeedsAttention;
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
        if (await LoginHealth.VerifiedApplicationSessionAsync(page, workflow.StartUrl, ct)
            .ConfigureAwait(false))
        {
            RequireCredentialAccess();
            RequireAutomationUrl(page.Url);
            Log($"Existing authenticated application confirmed for '{workflow.Name}'.");
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
        // The homepage is classified and navigated before a saved password is read.
        // A newsletter email field is never by itself evidence of a login form.
        await TryOpenLoginEntryAsync(workflow, page, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        RequireAutomationUrl(page.Url);
        if (workflow.UsernameSelector == StandardUsernameSelector)
        {
            await using var identifier = await ResolveAutomaticUsernameAsync(page, ct).ConfigureAwait(false);
            if (identifier is null &&
                !await HasSupportedResumedPasswordStageAsync(workflow, page, ct).ConfigureAwait(false))
            {
                Log($"Could not identify the sign-in form safely for '{workflow.Name}'. Continue in the browser.");
                return false;
            }
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

            // Default automatic discovery binds a single classified username field.
            // Deliberate custom selectors continue through the existing advanced path.
            var useDiscovery = workflow.UsernameSelector == StandardUsernameSelector;
            var autoUsername = requiresUser && useDiscovery
                ? await ResolveAutomaticUsernameAsync(page, ct).ConfigureAwait(false)
                : null;
            var passwordReady = requiresPassword &&
                await PasswordStageReadyAsync(workflow, page, ct).ConfigureAwait(false);
            var usernameReady = requiresUser && (useDiscovery
                ? autoUsername is not null
                : await LoginFieldReadyAsync(page, workflow.UsernameSelector, ct).ConfigureAwait(false));
            var submitted = false;
            var passwordHandledInsideUsername = false;
            try
            {
                if (requiresUser && (usernameReady || !passwordReady))
                {
                    if (!usernameReady && useDiscovery)
                    {
                        Log($"Could not identify the sign-in form safely for '{workflow.Name}'. Continue in the browser.");
                        return false;
                    }
                    var stageReached = true;
                    var selected = autoUsername;
                    autoUsername = null; // Transfer this handle to the protected fill boundary.
                    var usernameOrigin = await FillCredentialAsync(workflow, page, workflow.UsernameSelector,
                        credential.Username, ct, async owner =>
                        {
                            if (requiresPassword && useDiscovery)
                            {
                                // The chosen username form, not a page-wide
                                // password selector, owns the combined stage.
                                var (present, field) = await ResolveCombinedPasswordAsync(
                                    workflow, page, owner, ct).ConfigureAwait(false);
                                if (present)
                                {
                                    if (field is null) { stageReached = false; return; }
                                    var origin = await FillCredentialAsync(
                                        workflow, page, workflow.PasswordSelector, credential.Password, ct,
                                        async passwordOwner =>
                                        {
                                            if (string.IsNullOrWhiteSpace(workflow.SubmitSelector)) return;
                                            submitPageBeforeClick = page.Url;
                                            submitWasAttempted = true;
                                            submitted = await ClickLoginSubmitAsync(
                                                workflow, page, passwordOwner, ct).ConfigureAwait(false);
                                        }, field, expectedForm: owner.Form, isPassword: true).ConfigureAwait(false);
                                    passwordHandledInsideUsername = origin is not null;
                                    if (!passwordHandledInsideUsername) stageReached = false;
                                }
                                else
                                    stageReached = await AdvanceToPasswordStageAsync(
                                        workflow, page, owner, false, ct).ConfigureAwait(false);
                            }
                            else if (requiresPassword &&
                                !await PasswordStageReadyAsync(workflow, page, ct).ConfigureAwait(false))
                            {
                                var hadPasswordField = await PasswordStagePresentAsync(
                                    workflow, page, ct).ConfigureAwait(false);
                                if (!hadPasswordField ||
                                    !string.IsNullOrWhiteSpace(workflow.UsernameContinueSelector))
                                {
                                    if (string.IsNullOrWhiteSpace(workflow.UsernameContinueSelector) &&
                                        !string.IsNullOrWhiteSpace(workflow.SubmitSelector) &&
                                        workflow.SubmitSelector.StartsWith("#", StringComparison.Ordinal))
                                    {
                                        var configuredFinal = page.Locator(workflow.SubmitSelector);
                                        var finalOwned = await configuredFinal.CountAsync().ConfigureAwait(false) == 1 &&
                                            await configuredFinal.First.EvaluateAsync<bool>(
                                                "(button, form) => { const owner = 'form' in button ? button.form : button.closest('form'); return form !== null && owner === form; }",
                                                owner.Form).ConfigureAwait(false);
                                        if (!finalOwned)
                                            stageReached = await AdvanceToPasswordStageAsync(
                                                workflow, page, owner, hadPasswordField, ct).ConfigureAwait(false);
                                        else
                                            stageReached = false;
                                        if (finalOwned)
                                            Log($"A required password field is missing for '{workflow.Name}'. The configured final Submit will not be used as a first-stage continuation.");
                                    }
                                    else
                                        stageReached = await AdvanceToPasswordStageAsync(
                                            workflow, page, owner, hadPasswordField, ct).ConfigureAwait(false);
                                }
                            }
                            else if (!requiresPassword &&
                                     !string.IsNullOrWhiteSpace(workflow.SubmitSelector))
                            {
                                // Legacy username-only explicit submit remains owned by
                                // that filled identifier's exact document/form.
                                submitPageBeforeClick = page.Url;
                                submitWasAttempted = true;
                                submitted = await ClickLoginSubmitAsync(workflow, page, owner, ct)
                                    .ConfigureAwait(false);
                            }
                        }, selected).ConfigureAwait(false);
                    if (usernameOrigin is null || !stageReached)
                    {
                        Log($"Could not continue sign-in safely for '{workflow.Name}'. Sign in manually in the browser.");
                        return false;
                    }
                }
                else if (requiresUser && passwordReady)
                    Log($"Resuming password stage for '{workflow.Name}'.");

                if (requiresPassword && !passwordHandledInsideUsername)
                {
                    // A staged or resumed default login must be classified
                    // again in the current document. Never reuse a global
                    // first-match password from a registration/other form.
                    var selectedPassword = useDiscovery
                        ? await ResolveSupportedResumedPasswordStageAsync(
                            workflow, page, ct).ConfigureAwait(false)
                        : null;
                    if (useDiscovery && selectedPassword is null)
                    {
                        Log($"Could not identify the password stage for '{workflow.Name}'. Sign in manually.");
                        return false;
                    }
                    var passwordOrigin = await FillCredentialAsync(workflow, page, workflow.PasswordSelector,
                        credential.Password, ct, async owner =>
                        {
                            if (string.IsNullOrWhiteSpace(workflow.SubmitSelector)) return;
                            submitPageBeforeClick = page.Url;
                            submitWasAttempted = true;
                            submitted = await ClickLoginSubmitAsync(workflow, page, owner, ct)
                                .ConfigureAwait(false);
                        }, selectedPassword, isPassword: true).ConfigureAwait(false);
                    if (passwordOrigin is null)
                    {
                        Log($"Password field not found for '{workflow.Name}'. Complete sign-in in the browser.");
                        return false;
                    }
                }

                if ((!submitted && !string.IsNullOrWhiteSpace(workflow.SubmitSelector)) ||
                    (string.IsNullOrWhiteSpace(workflow.SubmitSelector) && requiresPassword))
                {
                    // Some SPAs transition during FillAsync. A missing submit is
                    // not a license to press an unrelated button or Enter.
                    if (await LoginCompletionObserver.ObserveVerifiedRedirectAsync(page, initialLoginUrl, 1500, ct)
                        .ConfigureAwait(false))
                    {
                        RequireCredentialAccess();
                        RequireAutomationUrl(page.Url);
                        return true;
                    }
                    Log($"No owned final sign-in action could be verified for '{workflow.Name}'. Sign in manually in the browser.");
                    return false;
                }
            }
            finally
            {
                if (autoUsername is not null)
                    await autoUsername.DisposeAsync().ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            var waitMs = Math.Clamp(workflow.LoginWaitSeconds, 5, 180) * 1000;
            if (!string.IsNullOrWhiteSpace(workflow.LoginSuccessSelector) ||
                !string.IsNullOrWhiteSpace(workflow.LoginSuccessUrlContains))
            {
                if (!await LoginCompletionObserver.ObserveLoginCompletionAsync(workflow, page,
                    submitPageBeforeClick, waitMs, ct).ConfigureAwait(false))
                {
                    Log($"Sign-in for '{workflow.Name}' was not verified. Check or repair the completion condition.");
                    return false;
                }
            }
            else if (!await LoginCompletionObserver.ObserveVerifiedApplicationOrRedirectAsync(
                page, workflow.StartUrl, initialLoginUrl,
                Math.Min(waitMs, 20_000), ct).ConfigureAwait(false))
            {
                Log($"Sign-in was submitted for '{workflow.Name}', but completion was not verified. Check the browser.");
                return false;
            }

            Log($"Sign-in verified for '{workflow.Name}'.");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Playwright may report that a submitted button detached during
            // successful navigation. Admit only a *verified* same-origin signed-in
            // redirect, never an arbitrary failed/blocked/MFA page.
            if (submitWasAttempted && submitPageBeforeClick is not null &&
                await LoginCompletionObserver.ObserveVerifiedApplicationOrRedirectAsync(page, workflow.StartUrl,
                    submitPageBeforeClick, 2000, ct).ConfigureAwait(false))
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
        WebsiteWorkflow workflow, IPage page, CredentialFieldBinding passwordOwner, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireCredentialAccess();
        RequireAutomationUrl(passwordOwner.Origin);
        var ownsForm = await passwordOwner.Field.EvaluateAsync<bool>(
            "(field, form) => form !== null && field.form === form",
            passwordOwner.Form).ConfigureAwait(false);
        // Default discovery must never click a page-wide no-form candidate.
        // A deliberate advanced selector can support an owned no-form SPA.
        var allowNoForm = !ownsForm && workflow.SubmitSelector != StandardSubmitSelector;
        if (!ownsForm && !allowNoForm) return false;

        var locator = page.Locator(workflow.SubmitSelector);
        var count = Math.Min(await locator.CountAsync().ConfigureAwait(false), 32);
        var candidates = new List<IElementHandle>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = locator.Nth(i);
                if (!await item.IsVisibleAsync().ConfigureAwait(false) ||
                    !await item.IsEnabledAsync().ConfigureAwait(false)) continue;
                if (workflow.SubmitSelector == StandardSubmitSelector)
                {
                    var label = await item.EvaluateAsync<string>(
                        "el => (el.getAttribute('aria-label') || el.innerText || el.textContent || el.value || '').trim()")
                        .ConfigureAwait(false);
                    if (!IsUsernameStageSubmitLabel(label)) continue;
                }
                var element = await item.ElementHandleAsync().ConfigureAwait(false);
                if (element is null) continue;
                var admitted = false;
                try
                {
                    admitted = !ownsForm || await element.EvaluateAsync<bool>(
                        "(button, form) => { const owner = 'form' in button ? button.form : button.closest('form'); return form !== null && owner === form; }",
                        passwordOwner.Form).ConfigureAwait(false);
                    if (admitted)
                    {
                        candidates.Add(element);
                        element = null;
                    }
                }
                finally
                {
                    if (element is not null) await element.DisposeAsync().ConfigureAwait(false);
                }
            }
            if (candidates.Count > 1)
            {
                Log($"Several final sign-in actions match '{workflow.Name}'. Continue in the browser.");
                return false;
            }
            if (candidates.Count == 1)
                return await ClickApprovedLoginElementAsync(page, candidates[0],
                    passwordOwner.Origin, ct, passwordOwner, ownsForm).ConfigureAwait(false);
        }
        finally
        {
            foreach (var candidate in candidates)
                await candidate.DisposeAsync().ConfigureAwait(false);
        }

        return await ClickUnambiguousFinalSubmitAsync(
            workflow, page, passwordOwner, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A login button may be clicked only on the exact document/origin that
    /// received the approved username or password. Never relay a submitted
    /// credential across an unapproved redirect, including an SSO redirect.
    /// </summary>
    private static async Task<bool> ClickApprovedLoginElementAsync(
        IPage page, IElementHandle element, string? approvedCredentialOrigin,
        CancellationToken ct, CredentialFieldBinding? firstStageOwner = null,
        bool requireOwnedForm = false)
    {
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
        // For username-stage actions, validate the original field/document/frame
        // after the async DOM inspections, immediately before protected dispatch.
        if (firstStageOwner is not null)
        {
            if (firstStageOwner.Frame.IsDetached ||
                !ReferenceEquals(await element.OwnerFrameAsync().ConfigureAwait(false), firstStageOwner.Frame) ||
                !ReferenceEquals(await firstStageOwner.Field.OwnerFrameAsync().ConfigureAwait(false), firstStageOwner.Frame))
                return false;
            if (!await firstStageOwner.Field.EvaluateAsync<bool>(
                "(field, document) => field.isConnected && field.ownerDocument === document && document.defaultView?.document === document",
                firstStageOwner.Document).ConfigureAwait(false)) return false;
            if (!await element.EvaluateAsync<bool>(
                "(button, document) => button.isConnected && button.ownerDocument === document && document.defaultView?.document === document",
                firstStageOwner.Document).ConfigureAwait(false)) return false;
            if (requireOwnedForm)
            {
                if (!await firstStageOwner.Field.EvaluateAsync<bool>(
                    "(field, form) => form !== null && field.form === form",
                    firstStageOwner.Form).ConfigureAwait(false)) return false;
                if (!await element.EvaluateAsync<bool>(
                    "(button, form) => { const owner = 'form' in button ? button.form : button.closest('form'); return form !== null && owner === form; }",
                    firstStageOwner.Form).ConfigureAwait(false)) return false;
            }
        }
        // DOM evaluation is awaitable. Re-check live authorization before ClickAsync.
        RequireCredentialAccess();
        RequireAutomationUrl(origin);
        ct.ThrowIfCancellationRequested();
        await element.ClickAsync().ConfigureAwait(false);
        RequireAutomationUrl(page.Url);
        return true;
    }

    // Scoped to the exact successful FillAsync call; handles are disposed by
    // FillCredentialAsync only after a same-document continuation has completed.
    private sealed record CredentialFieldBinding(
        string Origin, IElementHandle Field, IJSHandle Document, IJSHandle Form, IFrame Frame);

    private async Task<string?> FillCredentialAsync(
        WebsiteWorkflow workflow, IPage page, string selector, string value,
        CancellationToken ct, Func<CredentialFieldBinding, Task>? afterFill = null,
        IElementHandle? selectedElement = null, IJSHandle? expectedForm = null,
        bool isPassword = false)
    {
        ct.ThrowIfCancellationRequested();
        // Bind a concrete field before inserting a secret. Wait for JS-rendered
        // forms, but never allow that wait to retarget across a new document.
        await using var element = selectedElement ??
            await ResolveCredentialElementAsync(workflow, page, selector, ct).ConfigureAwait(false);
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
            if (isPassword) RequirePasswordDestination(workflow, origin);
            if (!await IsOriginAllowedAsync(workflow, origin).WaitAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("Credential origin was not approved. Complete sign-in manually.");
            // Approval can await a dialog while the page navigates or the bound field becomes unavailable.
            await element.WaitForElementStateAsync(ElementState.Visible).WaitAsync(ct).ConfigureAwait(false);
            await element.WaitForElementStateAsync(ElementState.Editable).WaitAsync(ct).ConfigureAwait(false);
            if (!OriginsMatch(origin, await VerifyAsync().ConfigureAwait(false)))
                throw new InvalidOperationException("Credential origin changed. Retry sign-in manually.");
            ct.ThrowIfCancellationRequested();
            // Capture the browser-computed HTML form owner before input events can
            // reparent the field. form= associations work even outside form ancestry.
            await using var ownerForm = afterFill is null ? null :
                await element.EvaluateHandleAsync("field => field.form").ConfigureAwait(false);
            // No awaited owner capture may bridge an authorization change
            // into a newly dispatched secret fill.
            if (!OriginsMatch(origin, await VerifyAsync().ConfigureAwait(false)))
                throw new InvalidOperationException("Credential origin changed. Retry sign-in manually.");
            if (expectedForm is not null &&
                !await element.EvaluateAsync<bool>(
                    "(field, form) => field.isConnected && form?.isConnected && field.form === form",
                    expectedForm).ConfigureAwait(false))
                throw new InvalidOperationException("Credential form changed. Retry sign-in manually.");
            if (!OriginsMatch(origin, await VerifyAsync().ConfigureAwait(false)))
                throw new InvalidOperationException("Credential origin changed. Retry sign-in manually.");
            ct.ThrowIfCancellationRequested();
            RequireCredentialAccess();
            RequireAutomationUrl(origin);
            if (isPassword) RequirePasswordDestination(workflow, origin);
            await element.FillAsync(value).ConfigureAwait(false);
            if (afterFill is not null && ownerForm is not null)
                await afterFill(new CredentialFieldBinding(
                    origin, element, document, ownerForm, frame)).ConfigureAwait(false);
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

    private static void RequirePasswordDestination(WebsiteWorkflow workflow, string fieldOrigin)
    {
        if (OriginsMatch(fieldOrigin, GetOrigin(workflow.StartUrl))) return;
        if (string.IsNullOrWhiteSpace(workflow.PasswordCredentialOrigin) ||
            !OriginsMatch(fieldOrigin, GetOrigin(workflow.PasswordCredentialOrigin)))
            throw new InvalidOperationException(
                "The password is not mapped to this identity provider. Sign in manually or choose the correct website credential.");
    }

    private async Task<bool> IsOriginAllowedAsync(WebsiteWorkflow workflow, string origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return false;

        if (!string.IsNullOrWhiteSpace(workflow.CredentialOrigin) &&
            OriginsMatch(origin, GetOrigin(workflow.CredentialOrigin)))
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
