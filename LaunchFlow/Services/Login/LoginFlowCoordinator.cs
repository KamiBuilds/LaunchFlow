using LaunchFlow.Models;

namespace LaunchFlow.Services;

/// <summary>
/// Owns only an individual login attempt's sequencing and transient nonsecret
/// submission markers. Credential acquisition and protected browser operations
/// remain live engine callbacks; none are retained between attempts.
/// </summary>
internal static class LoginFlowCoordinator
{
    internal static async Task<LoginFlowResult> RunAsync(
        Func<Task<bool>> inspectCurrentSession,
        Func<Task<bool>> discoverLoginEntry,
        Func<SiteCredential?> readAuthorizedCredential,
        Func<string> currentUrl,
        Func<SiteCredential, LoginAttemptState, Task<LoginStageOutcome>> executeStages,
        Func<LoginAttemptState, Task<bool>> verifyCompletion,
        Func<LoginAttemptState, Task<bool>> recoverAfterSubmission,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var attempt = new LoginAttemptState(currentUrl());
        if (await inspectCurrentSession().ConfigureAwait(false))
            return new LoginFlowResult(true, attempt);
        if (!await discoverLoginEntry().ConfigureAwait(false))
        {
            attempt.FailureCode ??= "no-form";
            return new LoginFlowResult(false, attempt);
        }

        var credential = readAuthorizedCredential();
        if (credential is null)
        {
            attempt.FailureCode = "missing-secrets";
            return new LoginFlowResult(false, attempt);
        }
        try
        {
            var result = await executeStages(credential, attempt).ConfigureAwait(false);
            return result switch
            {
                LoginStageOutcome.NeedsManual => new LoginFlowResult(false, attempt),
                LoginStageOutcome.VerifiedRedirect => new LoginFlowResult(true, attempt),
                LoginStageOutcome.Submitted => new LoginFlowResult(
                    await verifyCompletion(attempt).ConfigureAwait(false), attempt),
                _ => new LoginFlowResult(false, attempt)
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Only the engine's narrow post-submit navigation observation may
            // convert an exception to verified completion.
            return new LoginFlowResult(
                await recoverAfterSubmission(attempt).ConfigureAwait(false), attempt);
        }
    }
}

internal enum LoginStageOutcome { NeedsManual, Submitted, VerifiedRedirect }

internal sealed record LoginFlowResult(bool Succeeded, LoginAttemptState Attempt);

/// <summary>Fresh, nonsecret markers for one submitted sign-in attempt.</summary>
internal sealed class LoginAttemptState(string initialLoginUrl)
{
    public string InitialLoginUrl { get; } = initialLoginUrl;
    public string? SubmitPageBeforeClick { get; set; }
    public bool SubmitWasAttempted { get; set; }
    public bool Submitted { get; set; }
    public bool PasswordHandledInsideUsername { get; set; }
    public bool StageReached { get; set; } = true;
    public string? FailureCode { get; set; }
    public string ObservedRecipe { get; set; } = LoginRecipe.Unknown;
}
