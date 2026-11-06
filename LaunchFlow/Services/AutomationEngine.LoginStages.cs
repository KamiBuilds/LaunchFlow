using LaunchFlow.Models;
using Microsoft.Playwright;

namespace LaunchFlow.Services;

/// <summary>
/// Login stage implementation stays in the engine that owns browser contexts,
/// live policy checks and the document/form-bound credential fill boundary.
/// </summary>
public sealed partial class AutomationEngine
{
    private async Task<bool> CheckExistingAuthenticatedSessionAsync(
        WebsiteWorkflow workflow, IPage page, CancellationToken ct)
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
        return false;
    }

    private async Task<bool> PrepareLoginEntryAsync(
        WebsiteWorkflow workflow, IPage page, CancellationToken ct)
    {
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
        return true;
    }

    private async Task<LoginStageOutcome> ExecuteCredentialStagesAsync(
        WebsiteWorkflow workflow, IPage page, SiteCredential credential,
        LoginAttemptState state, CancellationToken ct)
    {
            var requiresUser = !string.IsNullOrWhiteSpace(workflow.UsernameSelector);
            var requiresPassword = !string.IsNullOrWhiteSpace(workflow.PasswordSelector);
            if ((!requiresUser && !requiresPassword) ||
                (requiresUser && string.IsNullOrWhiteSpace(credential.Username)) ||
                (requiresPassword && string.IsNullOrEmpty(credential.Password)))
            {
                Log($"Missing required saved sign-in details for '{workflow.Name}'. Sign in manually or update credentials.");
                return LoginStageOutcome.NeedsManual;
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
            try
            {
                if (requiresUser && (usernameReady || !passwordReady))
                {
                    if (!usernameReady && useDiscovery)
                    {
                        Log($"Could not identify the sign-in form safely for '{workflow.Name}'. Continue in the browser.");
                        return LoginStageOutcome.NeedsManual;
                    }
                    state.StageReached = true;
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
                                    if (field is null) { state.StageReached = false; return; }
                                    var origin = await FillCredentialAsync(
                                        workflow, page, workflow.PasswordSelector, credential.Password, ct,
                                        async passwordOwner =>
                                        {
                                            if (string.IsNullOrWhiteSpace(workflow.SubmitSelector)) return;
                                            state.SubmitPageBeforeClick = page.Url;
                                            state.SubmitWasAttempted = true;
                                            state.Submitted = await ClickLoginSubmitAsync(
                                                workflow, page, passwordOwner, ct).ConfigureAwait(false);
                                        }, field, expectedForm: owner.Form, isPassword: true).ConfigureAwait(false);
                                    state.PasswordHandledInsideUsername = origin is not null;
                                    if (!state.PasswordHandledInsideUsername) state.StageReached = false;
                                }
                                else
                                    state.StageReached = await AdvanceToPasswordStageAsync(
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
                                            state.StageReached = await AdvanceToPasswordStageAsync(
                                                workflow, page, owner, hadPasswordField, ct).ConfigureAwait(false);
                                        else
                                            state.StageReached = false;
                                        if (finalOwned)
                                            Log($"A required password field is missing for '{workflow.Name}'. The configured final Submit will not be used as a first-stage continuation.");
                                    }
                                    else
                                        state.StageReached = await AdvanceToPasswordStageAsync(
                                            workflow, page, owner, hadPasswordField, ct).ConfigureAwait(false);
                                }
                            }
                            else if (!requiresPassword &&
                                     !string.IsNullOrWhiteSpace(workflow.SubmitSelector))
                            {
                                // Legacy username-only explicit submit remains owned by
                                // that filled identifier's exact document/form.
                                state.SubmitPageBeforeClick = page.Url;
                                state.SubmitWasAttempted = true;
                                state.Submitted = await ClickLoginSubmitAsync(workflow, page, owner, ct)
                                    .ConfigureAwait(false);
                            }
                        }, selected).ConfigureAwait(false);
                    if (usernameOrigin is null || !state.StageReached)
                    {
                        Log($"Could not continue sign-in safely for '{workflow.Name}'. Sign in manually in the browser.");
                        return LoginStageOutcome.NeedsManual;
                    }
                }
                else if (requiresUser && passwordReady)
                    Log($"Resuming password stage for '{workflow.Name}'.");

                if (requiresPassword && !state.PasswordHandledInsideUsername)
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
                        return LoginStageOutcome.NeedsManual;
                    }
                    var passwordOrigin = await FillCredentialAsync(workflow, page, workflow.PasswordSelector,
                        credential.Password, ct, async owner =>
                        {
                            if (string.IsNullOrWhiteSpace(workflow.SubmitSelector)) return;
                            state.SubmitPageBeforeClick = page.Url;
                            state.SubmitWasAttempted = true;
                            state.Submitted = await ClickLoginSubmitAsync(workflow, page, owner, ct)
                                .ConfigureAwait(false);
                        }, selectedPassword, isPassword: true).ConfigureAwait(false);
                    if (passwordOrigin is null)
                    {
                        Log($"Password field not found for '{workflow.Name}'. Complete sign-in in the browser.");
                        return LoginStageOutcome.NeedsManual;
                    }
                }

                if ((!state.Submitted && !string.IsNullOrWhiteSpace(workflow.SubmitSelector)) ||
                    (string.IsNullOrWhiteSpace(workflow.SubmitSelector) && requiresPassword))
                {
                    // Some SPAs transition during FillAsync. A missing submit is
                    // not a license to press an unrelated button or Enter.
                    if (await LoginCompletionObserver.ObserveVerifiedRedirectAsync(page, state.InitialLoginUrl, 1500, ct)
                        .ConfigureAwait(false))
                    {
                        RequireCredentialAccess();
                        RequireAutomationUrl(page.Url);
                        return LoginStageOutcome.VerifiedRedirect;
                    }
                    Log($"No owned final sign-in action could be verified for '{workflow.Name}'. Sign in manually in the browser.");
                    return LoginStageOutcome.NeedsManual;
                }
            }
            finally
            {
                if (autoUsername is not null)
                    await autoUsername.DisposeAsync().ConfigureAwait(false);
            }

        return LoginStageOutcome.Submitted;
    }

    private async Task<bool> VerifySubmittedLoginAsync(
        WebsiteWorkflow workflow, IPage page, LoginAttemptState state, CancellationToken ct)
    {
            ct.ThrowIfCancellationRequested();
            var waitMs = Math.Clamp(workflow.LoginWaitSeconds, 5, 180) * 1000;
            if (!string.IsNullOrWhiteSpace(workflow.LoginSuccessSelector) ||
                !string.IsNullOrWhiteSpace(workflow.LoginSuccessUrlContains))
            {
                if (!await LoginCompletionObserver.ObserveLoginCompletionAsync(workflow, page,
                    state.SubmitPageBeforeClick, waitMs, ct).ConfigureAwait(false))
                {
                    Log($"Sign-in for '{workflow.Name}' was not verified. Check or repair the completion condition.");
                    return false;
                }
            }
            else if (!await LoginCompletionObserver.ObserveVerifiedApplicationOrRedirectAsync(
                page, workflow.StartUrl, state.InitialLoginUrl,
                Math.Min(waitMs, 20_000), ct).ConfigureAwait(false))
            {
                Log($"Sign-in was state.Submitted for '{workflow.Name}', but completion was not verified. Check the browser.");
                return false;
            }

            Log($"Sign-in verified for '{workflow.Name}'.");
            return true;
    }

    private async Task<bool> RecoverSubmittedNavigationAsync(
        WebsiteWorkflow workflow, IPage page, LoginAttemptState state, CancellationToken ct)
    {
            // Playwright may report that a state.Submitted button detached during
            // successful navigation. Admit only a *verified* same-origin signed-in
            // redirect, never an arbitrary failed/blocked/MFA page.
            if (state.SubmitWasAttempted && state.SubmitPageBeforeClick is not null &&
                await LoginCompletionObserver.ObserveVerifiedApplicationOrRedirectAsync(page, workflow.StartUrl,
                    state.SubmitPageBeforeClick, 2000, ct).ConfigureAwait(false))
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
