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
        var wantsFirefox = workflow.Browser.Contains("Firefox", StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows() && !BrowserInstallations.IsInstalled(workflow.Browser))
        {
            if (wantsFirefox)
                throw new InvalidOperationException(
                    "Firefox automatic sign-in needs LaunchFlow’s special Firefox (included with Setup, or Browser readiness → Install Firefox). " +
                    "Regular Firefox from Mozilla cannot do automatic sign-in. Or choose Edge, Chrome, Brave, Opera, or Vivaldi.");
            throw new InvalidOperationException(
                $"The selected browser ({workflow.Browser}) is not installed. Install it or pick another browser for this website.");
        }
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
        // Prefer the exact installed browser found on this PC (Edge, Chrome, Brave,
        // Opera, Opera GX, Vivaldi). Channel is only a fallback for Edge/Chrome.
        if (!wantsFirefox)
        {
            options.Args = ["--start-maximized"];
            var installed = BrowserInstallations.FindExecutable(workflow.Browser);
            if (installed is not null)
                options.ExecutablePath = installed;
            else
            {
                var channel = ResolveChannel(workflow.Browser);
                if (channel is not null) options.Channel = channel;
            }
        }
        // Firefox: never point ExecutablePath at mozilla.org firefox.exe — that
        // opens a blank window and never navigates. Use shipped automation Firefox.
        PlaywrightFirefoxSupport.ConfigureProcessBrowsersPath();
        if (wantsFirefox && !PlaywrightFirefoxSupport.IsAutomationFirefoxInstalled())
        {
            throw new InvalidOperationException(
                "Firefox automatic sign-in needs LaunchFlow’s special Firefox (included with Setup, or Browser readiness → Install Firefox). " +
                "Or choose Edge, Chrome, Brave, Opera, or Vivaldi.");
        }
        IBrowserContext context;
        try
        {
            context = await LaunchContextWithRecoveryAsync(browserType, workflow, profileDir, options, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (workflow.Browser.Contains("Firefox", StringComparison.OrdinalIgnoreCase) &&
                                   ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Could not start Firefox for automatic sign-in. Open Browser readiness → Install Firefox, " +
                "or use Edge, Chrome, Brave, Opera, or Vivaldi. Details: " + ex.GetType().Name, ex);
        }
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
            try { await page.BringToFrontAsync().ConfigureAwait(false); }
            catch (Exception) { /* bring-to-front is best effort */ }
            var monitors = MonitorService.GetMonitors();
            var monitor = MonitorService.Resolve(workflow, monitors);
            var bounds = monitor.Bounds;
            // Chromium-only CDP window placement; Firefox quietly skips.
            var cdp = await page.Context.NewCDPSessionAsync(page).ConfigureAwait(false);
            var windowJson = await cdp.SendAsync("Browser.getWindowForTarget").ConfigureAwait(false);
            if (!windowJson.HasValue) return;
            var windowId = windowJson.Value.GetProperty("windowId").GetInt32();
            // Place on the chosen monitor first, then maximize so the site fills
            // the screen instead of opening a small restored window.
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
            await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = windowId,
                ["bounds"] = new Dictionary<string, object>
                {
                    ["windowState"] = "maximized"
                }
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Log("Could not maximize the browser on the selected screen.");
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
        // Firefox Auto uses the automation Firefox build. Everything else we detect
        // (Edge, Chrome, Brave, Opera, Opera GX, Vivaldi) is Chromium-family.
        return browser.Contains("Firefox", StringComparison.OrdinalIgnoreCase)
            ? _playwright.Firefox
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
