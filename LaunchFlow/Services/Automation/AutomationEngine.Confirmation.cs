using System.IO;
using LaunchFlow.Models;
using Microsoft.Playwright;

namespace LaunchFlow.Services;

/// <summary>Explicit session-scoped user sign-in confirmation; kept in the
/// AutomationEngine partial type to preserve existing session ownership and policy guards.
/// </summary>
public sealed partial class AutomationEngine
{
    public async Task<bool> ConfirmSignedInAsync(WebsiteWorkflow workflow, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IPage page;
        lock (_gate)
        {
            if (_running || _closing || _cleanupRequired || _disposed ||
                !_sessions.TryGetValue(workflow.Id, out var session) || session.Page.IsClosed)
                return false;
            page = session.Page;
        }
        RequireCredentialAccess();
        var acknowledgment = new LoginAcknowledgment(page, page.Url);
        var retained = false;
        // Observe before the first inspection await, and retain the same observer
        // after acceptance. Even a same-address reload owns a different document.
        EventHandler<IFrame> invalidate = (_, frame) =>
        {
            if (!ReferenceEquals(frame, page.MainFrame)) return;
            lock (_gate)
            {
                acknowledgment.Navigated = true;
                if (_confirmedLogins.TryGetValue(workflow.Id, out var current) &&
                    ReferenceEquals(current, acknowledgment))
                    _confirmedLogins.Remove(workflow.Id);
            }
        };
        page.FrameNavigated += invalidate;
        try
        {
            if (!await SafeConfirmationPageAsync(workflow, page, ct).ConfigureAwait(false)) return false;
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                if (_running || _closing || _cleanupRequired || _disposed ||
                    acknowledgment.Navigated ||
                    !_sessions.TryGetValue(workflow.Id, out var current) ||
                    !ReferenceEquals(current.Page, page) || page.IsClosed ||
                    !string.Equals(page.Url, acknowledgment.Url, StringComparison.Ordinal) ||
                    !ConfirmationPolicyAllows(page))
                    return false;
                if (_confirmationNavigationHooks.Remove(workflow.Id, out var prior))
                    prior.Page.FrameNavigated -= prior.Handler;
                _confirmationNavigationHooks[workflow.Id] = (page, invalidate);
                _confirmedLogins[workflow.Id] = acknowledgment;
                retained = true;
            }
            Log($"User confirmed the signed-in state for '{workflow.Name}' in this browser session.");
            return true;
        }
        finally
        {
            if (!retained) page.FrameNavigated -= invalidate;
        }
    }

    private static async Task<bool> SafeConfirmationPageAsync(
        WebsiteWorkflow workflow, IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (page.IsClosed ||
                !Uri.TryCreate(workflow.StartUrl, UriKind.Absolute, out var start) ||
                !Uri.TryCreate(page.Url, UriKind.Absolute, out var current) ||
                current.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(start.Scheme, current.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(start.Authority, current.Authority, StringComparison.OrdinalIgnoreCase))
                return false;
            RequireAutomationUrl(page.Url);
            RequireCredentialAccess();
            var pathParts = current.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (pathParts.Any(part => part.Equals("login", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("signin", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("sign-in", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("mfa", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("challenge", StringComparison.OrdinalIgnoreCase)))
                return false;
            // Do not use unrelated dashboard text to infer a failure. Explicit
            // human confirmation is still rejected if authentication inputs
            // are visible or a one-time-code challenge remains on screen.
            if (await page.Locator("input[type='password']:visible").CountAsync().ConfigureAwait(false) > 0 ||
                await page.Locator("input[autocomplete='one-time-code']:visible").CountAsync().ConfigureAwait(false) > 0)
                return false;
            return await page.Locator("body").CountAsync().ConfigureAwait(false) > 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    private static bool ConfirmationPolicyAllows(IPage page)
    {
        try
        {
            RequireAutomationUrl(page.Url);
            RequireCredentialAccess();
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    private async Task<bool> ManuallyConfirmedAsync(WebsiteWorkflow workflow, IPage page, CancellationToken ct)
    {
        LoginAcknowledgment accepted;
        lock (_gate)
        {
            if (!_confirmedLogins.TryGetValue(workflow.Id, out accepted!) ||
                !ReferenceEquals(accepted.Page, page) ||
                !string.Equals(accepted.Url, page.Url, StringComparison.Ordinal))
                return false;
        }
        var inspected = await SafeConfirmationPageAsync(workflow, page, ct).ConfigureAwait(false);
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            // Inspection can pump navigation/cleanup callbacks. A local copy of
            // an acknowledgment must not outlive its exact dictionary owner.
            if (!_confirmedLogins.TryGetValue(workflow.Id, out var current) ||
                !ReferenceEquals(current, accepted))
                return false;
            if (!inspected || accepted.Navigated || _closing || _cleanupRequired || _disposed ||
                page.IsClosed || !_sessions.TryGetValue(workflow.Id, out var session) ||
                !ReferenceEquals(session.Page, page) ||
                !string.Equals(accepted.Url, page.Url, StringComparison.Ordinal) ||
                !ConfirmationPolicyAllows(page))
            {
                _confirmedLogins.Remove(workflow.Id);
                return false;
            }
            return true;
        }
    }

}
