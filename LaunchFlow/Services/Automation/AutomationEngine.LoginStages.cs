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
            if (identifier is null)
            {
                var resumed = LoginFieldDiscovery.UsesAutomaticPasswordDiscovery(workflow)
                    ? await HasSupportedResumedPasswordStageAsync(workflow, page, ct).ConfigureAwait(false)
                    : await HasConfiguredResumedPasswordStageAsync(workflow, page, ct).ConfigureAwait(false);
                if (!resumed)
                {
                    Log($"Could not identify the sign-in form safely for '{workflow.Name}'. Continue in the browser.");
                    return false;
                }
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
                state.FailureCode = "missing-secrets";
                Log($"Missing required saved sign-in details for '{workflow.Name}'. Sign in manually or update credentials.");
                return LoginStageOutcome.NeedsManual;
            }

            var remembered = LoginRecipe.Normalize(workflow.LoginRecipe);
            // Discovery policy is independent for each selector. Field ownership
            // is not: a classified username still binds the password to its form
            // even when the password selector is custom.
            var discoverUsername = LoginFieldDiscovery.UsesAutomaticUsernameDiscovery(workflow);
            var discoverPassword = LoginFieldDiscovery.UsesAutomaticPasswordDiscovery(workflow);
            if (!discoverUsername && !discoverPassword)
                state.ObservedRecipe = LoginRecipe.CustomSelectors;
            var autoUsername = requiresUser && discoverUsername
                ? await ResolveAutomaticUsernameAsync(page, ct).ConfigureAwait(false)
                : null;
            var passwordReady = requiresPassword &&
                await PasswordStageReadyAsync(workflow, page, ct).ConfigureAwait(false);
            var usernameReady = requiresUser && (discoverUsername
                ? autoUsername is not null
                : await LoginFieldReadyAsync(page, workflow.UsernameSelector, ct).ConfigureAwait(false));
            try
            {
                if (requiresUser && (usernameReady || !passwordReady))
                {
                    if (!usernameReady && discoverUsername)
                    {
                        state.FailureCode = "no-form";
                        Log($"Could not identify the sign-in form safely for '{workflow.Name}'. Continue in the browser.");
                        return LoginStageOutcome.NeedsManual;
                    }
                    state.StageReached = true;
                    var selected = autoUsername;
                    autoUsername = null; // Transfer this handle to the protected fill boundary.
                    var usernameOrigin = await FillCredentialAsync(workflow, page, workflow.UsernameSelector,
                        credential.Username, ct,
                        owner => ContinueUsernameWithinOwnerAsync(
                            workflow, page, credential, owner, state, requiresPassword,
                            discoverPassword, remembered, ct),
                        selected).ConfigureAwait(false);
                    if (usernameOrigin is null || !state.StageReached)
                    {
                        state.FailureCode ??= state.ObservedRecipe == LoginRecipe.EmailThenPassword
                            ? "no-password-stage" : "no-form";
                        Log($"Could not continue sign-in safely for '{workflow.Name}'. Sign in manually in the browser.");
                        return LoginStageOutcome.NeedsManual;
                    }
                }
                else if (requiresUser && passwordReady)
                    Log($"Resuming password stage for '{workflow.Name}'.");

                if (requiresPassword && !state.PasswordHandledInsideUsername)
                {
                    // Automatic passwords require login-purpose classification.
                    // An explicitly configured selector uses an exact, unique bound
                    // element instead; its readiness and origin approval remain
                    // inside FillCredentialAsync, including for no-form SPAs.
                    var selectedPassword = discoverPassword
                        ? await ResolveSupportedResumedPasswordStageAsync(workflow, page, ct).ConfigureAwait(false)
                        : await ResolveConfiguredPasswordStageAsync(workflow, page, ct).ConfigureAwait(false);
                    if (selectedPassword is null)
                    {
                        state.FailureCode = "no-password-stage";
                        Log($"Could not identify the password stage for '{workflow.Name}'. Sign in manually.");
                        return LoginStageOutcome.NeedsManual;
                    }
                    if (requiresUser && state.ObservedRecipe != LoginRecipe.SamePage)
                        state.ObservedRecipe = LoginRecipe.EmailThenPassword;
                    var passwordOrigin = await FillCredentialAsync(workflow, page, workflow.PasswordSelector,
                        credential.Password, ct, owner => SubmitFinalBoundAsync(workflow, page, owner, state, ct), selectedPassword, isPassword: true).ConfigureAwait(false);
                    if (passwordOrigin is null)
                    {
                        state.FailureCode = "no-password-field";
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
                    state.FailureCode = "no-final-submit";
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

    /// <summary>
    /// Runs synchronously in the lifetime of FillCredentialAsync's afterFill
    /// callback. The owner holds the exact inserted field/document/form/frame;
    /// neither this method nor the coordinator retain or return those handles.
    /// </summary>
    private async Task ContinueUsernameWithinOwnerAsync(
        WebsiteWorkflow workflow, IPage page, SiteCredential credential,
        CredentialFieldBinding owner, LoginAttemptState state, bool requiresPassword,
        bool discoverPassword, string rememberedRecipe, CancellationToken ct)
    {
        if (!requiresPassword)
        {
            if (!string.IsNullOrWhiteSpace(workflow.SubmitSelector))
                await SubmitFinalBoundAsync(workflow, page, owner, state, ct).ConfigureAwait(false);
            return;
        }

        var hasForm = await owner.Field.EvaluateAsync<bool>(
            "field => field.isConnected && field.form !== null && field.form.isConnected")
            .ConfigureAwait(false);
        var passwordReady = await PasswordStageReadyAsync(workflow, page, ct).ConfigureAwait(false);

        // The current bound form takes precedence over a remembered staged
        // recipe. FillCredentialAsync owns waiting for its selected password.
        if (hasForm)
        {
            // A concrete username form always owns its same-page password,
            // irrespective of whether the password CSS was customized.
            var (present, field) = await ResolveCombinedPasswordAsync(
                workflow, page, owner, discoverPassword, ct).ConfigureAwait(false);
            if (present)
            {
                if (field is null)
                {
                    state.FailureCode = "no-password-field";
                    state.StageReached = false;
                    return;
                }
                // An explicit first-stage action may be required to reveal an
                // existing same-form password. Dispatch it only while the
                // selected password is not actionable; otherwise keep the exact
                // bound field and let the protected fill boundary await readiness.
                if (!string.IsNullOrWhiteSpace(workflow.UsernameContinueSelector) &&
                    (!await field.IsVisibleAsync().ConfigureAwait(false) ||
                     !await field.IsEditableAsync().ConfigureAwait(false)))
                {
                    await field.DisposeAsync().ConfigureAwait(false);
                    state.StageReached = await AdvanceToPasswordStageAsync(
                        workflow, page, owner, hadPasswordField: true, ct).ConfigureAwait(false);
                    if (state.StageReached)
                        state.ObservedRecipe = LoginRecipe.EmailThenPassword;
                    else
                        state.FailureCode = "no-password-stage";
                    return;
                }
                var origin = await FillCredentialAsync(
                    workflow, page, workflow.PasswordSelector, credential.Password, ct,
                    passwordOwner => SubmitFinalBoundAsync(workflow, page, passwordOwner, state, ct),
                    field, expectedForm: owner.Form, isPassword: true).ConfigureAwait(false);
                state.PasswordHandledInsideUsername = origin is not null;
                if (state.PasswordHandledInsideUsername)
                    state.ObservedRecipe = LoginRecipe.SamePage;
                else
                    state.StageReached = false;
                return;
            }
        }
        else if (!hasForm && !discoverPassword && passwordReady &&
                 string.IsNullOrWhiteSpace(workflow.UsernameContinueSelector))
        {
            // A deliberate no-form selector may target another field in the
            // same document. It may not jump to another document or iframe.
            var field = await ResolveConfiguredPasswordStageAsync(workflow, page, ct)
                .ConfigureAwait(false);
            if (field is not null)
            {
                if (!ReferenceEquals(await field.OwnerFrameAsync().ConfigureAwait(false), owner.Frame) ||
                    !await field.EvaluateAsync<bool>(
                        "(field, document) => field.isConnected && field.ownerDocument === document && document.defaultView?.document === document",
                        owner.Document).ConfigureAwait(false))
                {
                    await field.DisposeAsync().ConfigureAwait(false);
                    state.FailureCode = "no-password-field";
                    state.StageReached = false;
                    return;
                }
                var origin = await FillCredentialAsync(
                    workflow, page, workflow.PasswordSelector, credential.Password, ct,
                    passwordOwner => SubmitFinalBoundAsync(workflow, page, passwordOwner, state, ct),
                    field, expectedDocument: owner.Document, isPassword: true).ConfigureAwait(false);
                state.PasswordHandledInsideUsername = origin is not null;
                if (state.PasswordHandledInsideUsername)
                    state.ObservedRecipe = LoginRecipe.SamePage;
                else
                    state.StageReached = false;
                return;
            }
            // An ambiguous or unsafe explicit selector must not reach the
            // next-stage global password path without an owned continuation.
            state.FailureCode = "no-password-field";
            state.StageReached = false;
            return;
        }

        if (rememberedRecipe == LoginRecipe.SamePage)
        {
            state.FailureCode = "no-password-field";
            state.StageReached = false;
            return;
        }

        // No matching password belongs to the current form. Advancing requires
        // an owned first-stage action, not an unrelated page-wide password.
        var hadPasswordField = hasForm
            ? await owner.Field.EvaluateAsync<bool>(
                "field => Array.from(field.form.elements).some(el => el instanceof HTMLInputElement && el.type === 'password')")
                .ConfigureAwait(false)
            : await PasswordStagePresentAsync(workflow, page, ct).ConfigureAwait(false);
        if (hasForm && hadPasswordField &&
            string.IsNullOrWhiteSpace(workflow.UsernameContinueSelector))
        {
            state.FailureCode = "no-password-field";
            state.StageReached = false;
            Log($"The configured password is not in the current sign-in form for '{workflow.Name}'. Sign in manually.");
            return;
        }
        if (string.IsNullOrWhiteSpace(workflow.UsernameContinueSelector) &&
            !string.IsNullOrWhiteSpace(workflow.SubmitSelector) &&
            workflow.SubmitSelector.StartsWith("#", StringComparison.Ordinal))
        {
            var configuredFinal = page.Locator(workflow.SubmitSelector);
            var finalOwned = await configuredFinal.CountAsync().ConfigureAwait(false) == 1 &&
                await configuredFinal.First.EvaluateAsync<bool>(
                    "(button, form) => { const candidate = 'form' in button ? button.form : button.closest('form'); return form !== null && candidate === form; }",
                    owner.Form).ConfigureAwait(false);
            if (finalOwned)
            {
                state.FailureCode = "no-password-field";
                state.StageReached = false;
                Log($"A required password field is missing for '{workflow.Name}'. The configured final Submit will not be used as a first-stage continuation.");
                return;
            }
        }
        state.StageReached = await AdvanceToPasswordStageAsync(
            workflow, page, owner, hadPasswordField, ct).ConfigureAwait(false);
        if (state.StageReached)
            state.ObservedRecipe = LoginRecipe.EmailThenPassword;
        else
            state.FailureCode ??= "no-password-stage";
    }

    private async Task SubmitFinalBoundAsync(
        WebsiteWorkflow workflow, IPage page, CredentialFieldBinding owner,
        LoginAttemptState state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workflow.SubmitSelector)) return;
        state.SubmitPageBeforeClick = page.Url;
        state.SubmitWasAttempted = true;
        state.Submitted = await ClickLoginSubmitAsync(workflow, page, owner, ct)
            .ConfigureAwait(false);
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
                Log($"Sign-in was submitted for '{workflow.Name}', but completion was not verified. Check the browser.");
                return false;
            }

            Log($"Sign-in verified for '{workflow.Name}'.");
            return true;
    }

    private async Task<bool> RecoverSubmittedNavigationAsync(
        WebsiteWorkflow workflow, IPage page, LoginAttemptState state, CancellationToken ct)
    {
            // Playwright may report that a submitted button detached during
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
