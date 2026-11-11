using LaunchFlow.Models;
using Microsoft.Playwright;

namespace LaunchFlow.Services;

/// <summary>Per-attempt completion observation. This object neither owns a
/// browser session nor carries a persistent credential or authorization.</summary>
internal static class LoginCompletionObserver
{
    internal static async Task<bool> ObserveVerifiedApplicationOrRedirectAsync(
        IPage page, string applicationUrl, string submittedFrom, int maxWaitMs, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(maxWaitMs);
        do
        {
            ct.ThrowIfCancellationRequested();
            if (await LoginHealth.VerifiedApplicationSessionAsync(page, applicationUrl, ct)
                .ConfigureAwait(false) ||
                await LoginHealth.VerifiedRedirectAsync(page, submittedFrom, ct)
                .ConfigureAwait(false))
                return true;
            if (DateTimeOffset.UtcNow >= deadline) return false;
            await Task.Delay(200, ct).ConfigureAwait(false);
        } while (true);
    }

    internal static async Task<bool> ObserveVerifiedRedirectAsync(
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

    internal static async Task<bool> ObserveLoginCompletionAsync(
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
                if (selectorReady && urlReady &&
                    !string.IsNullOrWhiteSpace(workflow.LoginSuccessSelector) &&
                    !string.Equals(workflow.LoginSuccessSelector.Trim(), "body", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(workflow.LoginSuccessSelector.Trim(), "html", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (urlReady && await LoginHealth.VerifiedApplicationSessionAsync(
                    page, workflow.StartUrl, ct).ConfigureAwait(false)) return true;
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

}
