using LaunchFlow.Models;

namespace LaunchFlow.Services;

/// <summary>
/// Owns only an individual login attempt's sequencing and transient nonsecret
/// submission markers. Credential acquisition and protected browser operations
/// remain live engine callbacks; none are retained between attempts.
/// </summary>
internal static class LoginFlowCoordinator
{
    internal static async Task<bool> RunAsync(
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
        if (await inspectCurrentSession().ConfigureAwait(false)) return true;
        if (!await discoverLoginEntry().ConfigureAwait(false)) return false;

        var credential = readAuthorizedCredential();
        if (credential is null) return false;
        var attempt = new LoginAttemptState(currentUrl());
        try
        {
            var result = await executeStages(credential, attempt).ConfigureAwait(false);
            return result switch
            {
                LoginStageOutcome.NeedsManual => false,
                LoginStageOutcome.VerifiedRedirect => true,
                LoginStageOutcome.Submitted => await verifyCompletion(attempt).ConfigureAwait(false),
                _ => false
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Only the engine's narrow post-submit navigation observation may
            // convert an exception to verified completion.
            return await recoverAfterSubmission(attempt).ConfigureAwait(false);
        }
    }
}

internal enum LoginStageOutcome { NeedsManual, Submitted, VerifiedRedirect }

/// <summary>Fresh, nonsecret markers for one submitted sign-in attempt.</summary>
internal sealed class LoginAttemptState(string initialLoginUrl)
{
    public string InitialLoginUrl { get; } = initialLoginUrl;
    public string? SubmitPageBeforeClick { get; set; }
    public bool SubmitWasAttempted { get; set; }
    public bool Submitted { get; set; }
    public bool PasswordHandledInsideUsername { get; set; }
    public bool StageReached { get; set; } = true;
}
