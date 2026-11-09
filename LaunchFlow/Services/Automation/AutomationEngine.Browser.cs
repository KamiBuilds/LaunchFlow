using System.IO;
using LaunchFlow.Models;
using Microsoft.Playwright;

namespace LaunchFlow.Services;

public sealed partial class AutomationEngine
{
    private async Task<IPage> GetOrCreatePageAsync(WebsiteWorkflow workflow, CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate) CheckSessionAdmission();
            await EnsurePlaywrightAsync().ConfigureAwait(false);
            return await GetOrCreatePageCoreAsync(workflow, ct).ConfigureAwait(false);
        }
        finally { _sessionGate.Release(); }
    }

    private async Task<IPage> GetOrCreatePageCoreAsync(WebsiteWorkflow workflow, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireAutomationUrl(workflow.StartUrl);
        if (_sessions.TryGetValue(workflow.Id, out var existing))
        {
            if (!existing.Page.IsClosed) return existing.Page;
            try
            {
                await existing.Context.CloseAsync().ConfigureAwait(false);
                _sessions.Remove(workflow.Id);
            }
            catch
            {
                lock (_gate) _cleanupRequired = true;
                throw new InvalidOperationException("The previous browser session could not be closed. Retry closing sessions before continuing.");
            }
        }

        // A saved browser may have been uninstalled since the website was added.
        // Never silently launch a different browser with a different profile.
        if (OperatingSystem.IsWindows() && !BrowserInstallations.IsInstalled(workflow.Browser))
            throw new InvalidOperationException(
                $"The selected browser ({workflow.Browser}) is not installed. Install it or select an available browser in website settings.");
        var browserType = ResolveBrowserType(workflow.Browser);
        var profileDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaunchFlow", "profiles-v2", workflow.Id.ToString("N"));
        // Do not replace a failed restore with an empty browser profile and overwrite its sealed data on the next lock.
        var sealedPath = Path.Combine(ProfileVaultService.SealedRoot, workflow.Id.ToString("N") + ".profile.aes");
        if (!Directory.Exists(profileDir) && File.Exists(sealedPath))
            throw new InvalidOperationException("This browser profile could not be restored. Its encrypted files were retained; do not replace them.");
        Directory.CreateDirectory(profileDir);

        var options = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = false,
            ViewportSize = ViewportSize.NoViewport
        };
        var channel = ResolveChannel(workflow.Browser);
        if (channel is not null) options.Channel = channel;
        // Use Playwright's standard supported browser automation configuration.
        var context = await LaunchContextWithRecoveryAsync(browserType, workflow, profileDir, options, ct).ConfigureAwait(false);
        _unclaimedContexts.Add(context);
        try
        {
            // Launch has no cancellation-token API. Own its result before inspecting or publishing any page.
            ct.ThrowIfCancellationRequested();
            lock (_gate) CheckSessionAdmission();
            RequireAutomationUrl(workflow.StartUrl);
            var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync().ConfigureAwait(false);
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                CheckSessionAdmission();
                _sessions[workflow.Id] = new TrackedSession(context, page);
                _unclaimedContexts.Remove(context);
            }
            return page;
        }
        catch
        {
            try
            {
                await context.CloseAsync().ConfigureAwait(false);
                _unclaimedContexts.Remove(context);
            }
            catch { lock (_gate) _cleanupRequired = true; }
            throw;
        }
    }

    private async Task<IBrowserContext> LaunchContextWithRecoveryAsync(
        IBrowserType browserType,
        WebsiteWorkflow workflow,
        string profileDir,
        BrowserTypeLaunchPersistentContextOptions options,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return await browserType.LaunchPersistentContextAsync(profileDir, options).ConfigureAwait(false);
        }
        catch (Exception ex) when (LooksLikeProfileInUse(ex))
        {
            // An active profile belongs to its running browser. Removing locks or copying SQLite files can corrupt it.
            throw new InvalidOperationException(
                $"The browser profile for '{workflow.Name}' is already in use. Close that workflow's browser window and try again.", ex);
        }
    }

    private static bool LooksLikeProfileInUse(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("existing browser session", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("profile is already in use", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("singleton", StringComparison.OrdinalIgnoreCase);
    }

    private async Task PositionWindowAsync(IPage page, WebsiteWorkflow workflow)
    {
        try
        {
            var monitors = MonitorService.GetMonitors();
            var monitor = MonitorService.Resolve(workflow, monitors);
            var bounds = monitor.Bounds;
            // Chromium-only CDP window placement; Firefox quietly skips.
            var cdp = await page.Context.NewCDPSessionAsync(page).ConfigureAwait(false);
            var windowJson = await cdp.SendAsync("Browser.getWindowForTarget").ConfigureAwait(false);
            if (!windowJson.HasValue) return;
            var windowId = windowJson.Value.GetProperty("windowId").GetInt32();
            await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = windowId,
                ["bounds"] = new Dictionary<string, object>
                {
                    ["left"] = bounds.Left,
                    ["top"] = bounds.Top,
                    ["width"] = Math.Max(1, bounds.Width),
                    ["height"] = Math.Max(1, bounds.Height),
                    ["windowState"] = "normal"
                }
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Log("Could not position browser on the selected monitor.");
        }
    }

    private async Task EnsurePlaywrightAsync()
    {
        if (_playwright is not null) return;
        _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
    }

    private IBrowserType ResolveBrowserType(string browser)
    {
        if (_playwright is null) throw new InvalidOperationException("Playwright is not initialized.");
        return browser.Contains("Firefox", StringComparison.OrdinalIgnoreCase)
            ? _playwright.Firefox
            : browser.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
              browser.Contains("Edge", StringComparison.OrdinalIgnoreCase)
                ? _playwright.Chromium
                : _playwright.Chromium;
    }

    private static string? ResolveChannel(string browser)
    {
        if (browser.Contains("Edge", StringComparison.OrdinalIgnoreCase)) return "msedge";
        if (browser.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "chrome";
        return null;
    }

    private static bool IsDefaultBrowserOpenOnly(string browser) =>
        browser.Contains("Default", StringComparison.OrdinalIgnoreCase) ||
        browser.Contains("open only", StringComparison.OrdinalIgnoreCase);
}
