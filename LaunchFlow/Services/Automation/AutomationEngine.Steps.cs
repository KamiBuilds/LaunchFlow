using System.IO;
using LaunchFlow.Models;
using Microsoft.Playwright;

namespace LaunchFlow.Services;

/// <summary>Validated website-step playback; kept in the
/// AutomationEngine partial type to preserve existing session ownership and policy guards.
/// </summary>
public sealed partial class AutomationEngine
{
    private static async Task ExecuteStepAsync(IPage page, WorkflowStep step, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireAutomationUrl(page.Url);
        if (step.Type == "Fill" && EnterprisePolicy.Current.DisableCredentialFill)
            throw new InvalidOperationException("Fill actions are disabled by your organization's policy.");
        switch (step.Type)
        {
            case "Navigate":
                ValidateUrl(step.Target);
                RequireAutomationUrl(step.Target);
                await page.GotoAsync(step.Target, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60_000
                }).ConfigureAwait(false);
                break;
            case "Click":
                await ExecuteElementActionAsync(page, step, ct).ConfigureAwait(false);
                break;
            case "Fill":
                await ExecuteElementActionAsync(page, step, ct).ConfigureAwait(false);
                break;
            case "Press":
                await ExecutePressAsync(page, step, ct).ConfigureAwait(false);
                break;
            case "Wait":
                if (int.TryParse(step.Target, out var ms))
                    await Task.Delay(Math.Clamp(ms, 0, 60_000), ct).ConfigureAwait(false);
                else
                    await page.Locator(step.Target).First.WaitForAsync(
                        new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 })
                        .WaitAsync(ct).ConfigureAwait(false);
                break;
            case "AssertVisible":
                if (!await page.Locator(step.Target).First.IsVisibleAsync().ConfigureAwait(false))
                    throw new InvalidOperationException("Expected element is not visible.");
                break;
            case "WaitForURL":
                ValidateUrl(step.Target);
                RequireAutomationUrl(step.Target);
                await page.WaitForURLAsync(url =>
                    string.Equals(url, step.Target, StringComparison.OrdinalIgnoreCase),
                    new PageWaitForURLOptions { Timeout = 60_000 }).WaitAsync(ct).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException("Unsupported workflow step.");
        }
        RequireAutomationUrl(page.Url);
    }

    private static async Task ExecuteElementActionAsync(IPage page, WorkflowStep step, CancellationToken ct)
    {
        // Resolve once, then authorize the document owning that element. Navigation cannot retarget this handle.
        // Playwright's normal locator wait handles most delayed elements.
        // Extra waits belong in an explicit Wait step so broken selectors
        // do not silently stall every website for an additional 45 seconds.
        await using var element = await page.Locator(step.Target).First.ElementHandleAsync()
            .WaitAsync(ct).ConfigureAwait(false);
        if (element is null) throw new InvalidOperationException("Workflow action target is unavailable.");
        ct.ThrowIfCancellationRequested();
        var url = await element.EvaluateAsync<string>("""
            element => {
                const document = element.ownerDocument;
                if (!element.isConnected || document.defaultView?.document !== document)
                    throw new Error('Workflow document changed');
                return document.location.href;
            }
            """).ConfigureAwait(false);
        RequireAutomationUrl(url);
        ct.ThrowIfCancellationRequested();
        if (step.Type == "Fill")
        {
            if (EnterprisePolicy.Current.DisableCredentialFill)
                throw new InvalidOperationException("Fill actions are disabled by your organization's policy.");
            var inputType = await element.GetAttributeAsync("type").ConfigureAwait(false);
            var autocomplete = await element.GetAttributeAsync("autocomplete").ConfigureAwait(false);
            if (string.Equals(inputType, "password", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(autocomplete, "current-password", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(autocomplete, "new-password", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Use vault credentials instead of storing passwords in workflow steps.");
            ct.ThrowIfCancellationRequested();
            // Attribute inspection awaits can race a live policy change; recheck
            // before this separate fill dispatch.
            RequireAutomationUrl(url);
            if (EnterprisePolicy.Current.DisableCredentialFill)
                throw new InvalidOperationException("Fill actions are disabled by your organization's policy.");
            if (EnterprisePolicy.Current.DisableAutomation)
                throw new InvalidOperationException("Automation is disabled by your organization's policy.");
            await element.FillAsync(step.Value ?? "").ConfigureAwait(false);
        }
        else await element.ClickAsync().ConfigureAwait(false);
    }

    private static async Task ExecutePressAsync(IPage page, WorkflowStep step, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Keyboard input follows the deepest focused frame owner (nested iframes /
        // cross-origin grandchildren), not only top document or one iframe hop.
        var focusedUrl = await page.EvaluateAsync<string?>("""
            () => {
              // Walk open shadowRoot.activeElement and nested iframe contentDocument.
              // Shadow hosts are not frames: stop only when focus is a non-frame leaf.
              const deepest = (root) => {
                let active = root.activeElement;
                while (active && active.shadowRoot && active.shadowRoot.activeElement)
                  active = active.shadowRoot.activeElement;
                if (!active) {
                  try { return root.defaultView ? root.defaultView.location.href : null; }
                  catch { return null; }
                }
                if (active.tagName === 'IFRAME' || active.tagName === 'FRAME') {
                  try {
                    const child = active.contentDocument;
                    if (!child) return null; // cross-origin / unreadable — fail closed
                    return deepest(child);
                  } catch { return null; }
                }
                try {
                  const doc = active.ownerDocument || root;
                  return doc.defaultView ? doc.defaultView.location.href : doc.location.href;
                } catch { return null; }
              };
              try { return deepest(document); }
              catch { return null; }
            }
            """).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(focusedUrl))
            throw new InvalidOperationException("Keyboard focus is in a frame LaunchFlow cannot authorize.");
        RequireAutomationUrl(focusedUrl);
        ct.ThrowIfCancellationRequested();
        await page.Keyboard.PressAsync(step.Target).ConfigureAwait(false);
    }

}
