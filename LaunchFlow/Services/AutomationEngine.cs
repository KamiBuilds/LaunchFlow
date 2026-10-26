using System.IO;
using LaunchFlow.Models;
using Microsoft.Playwright;

namespace LaunchFlow.Services;

public sealed class AutomationEngine : IAsyncDisposable
{
    private readonly CredentialVault _vault;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TrackedSession> _sessions = new();
    private IPlaywright? _playwright;
    private bool _running;

    public event Action<string>? OnLog;
    public Func<WebsiteWorkflow, string, Task<bool>>? ApproveLoginOriginAsync { get; set; }

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public sealed record LoginAttention(Guid WorkflowId, string WorkflowName, string Reason);

    public sealed class RunResult
    {
        public List<LoginAttention> LoginNeedsAttention { get; } = new();
        public int FinishedCount { get; set; }
        public int FailedCount { get; set; }
    }

    public AutomationEngine(CredentialVault vault)
    {
        _vault = vault;
    }

    public static void ValidateUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is required.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"Invalid URL: {url}");
    }

    public async Task<RunResult> RunAsync(
        IEnumerable<WebsiteWorkflow> workflows,
        CancellationToken cancellationToken,
        bool includeDisabled = false,
        Configuration? configuration = null,
        string? keepassMasterPassword = null)
    {
        lock (_gate)
        {
            if (_running)
                throw new InvalidOperationException("Automation is already running.");
            _running = true;
        }

        var result = new RunResult();
        try
        {
            await EnsurePlaywrightAsync().ConfigureAwait(false);
            foreach (var workflow in workflows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!includeDisabled && !workflow.Enabled) continue;
                try
                {
                    var attention = await RunOneAsync(workflow, cancellationToken, configuration, keepassMasterPassword)
                        .ConfigureAwait(false);
                    if (attention is not null)
                        result.LoginNeedsAttention.Add(attention);
                    else
                        result.FinishedCount++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.FailedCount++;
                    Log($"Workflow '{workflow.Name}' failed: {ex.Message}");
                }
            }
        }
        finally
        {
            lock (_gate) _running = false;
        }

        return result;
    }

    public async Task<IPage> GetPageForRecorderAsync(WebsiteWorkflow workflow)
    {
        await EnsurePlaywrightAsync().ConfigureAwait(false);
        var page = await GetOrCreatePageAsync(workflow, CancellationToken.None).ConfigureAwait(false);
        ValidateUrl(workflow.StartUrl);
        // Keep any sign-in progress in the browser during Teach or Repair.
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var current) ||
            (current.Scheme != Uri.UriSchemeHttp && current.Scheme != Uri.UriSchemeHttps))
        {
            await page.GotoAsync(workflow.StartUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            }).ConfigureAwait(false);
        }
        return page;
    }

    /// <summary>
    /// Close browser contexts so profile folders can be sealed on vault lock.
    /// Playwright itself stays alive for the next unlock.
    /// </summary>
    public async Task CloseSessionsAsync()
    {
        lock (_gate) _running = false;
        foreach (var session in _sessions.Values.ToList())
        {
            try { await session.Context.CloseAsync().ConfigureAwait(false); } catch { /* lock path */ }
        }
        _sessions.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseSessionsAsync().ConfigureAwait(false);
        _playwright?.Dispose();
        _playwright = null;
    }

    private async Task<LoginAttention?> RunOneAsync(
        WebsiteWorkflow workflow,
        CancellationToken ct,
        Configuration? configuration,
        string? keepassMasterPassword)
    {
        Log($"Starting '{workflow.Name}'…");
        ValidateUrl(workflow.StartUrl);
        if (!string.IsNullOrWhiteSpace(workflow.DestinationUrl))
            ValidateUrl(workflow.DestinationUrl);

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
        await PositionWindowAsync(page, workflow.MonitorIndex).ConfigureAwait(false);

        var currentUrl = page.Url;
        var alreadyOnSite = !string.IsNullOrEmpty(currentUrl) &&
                            Uri.TryCreate(currentUrl, UriKind.Absolute, out var cur) &&
                            Uri.TryCreate(workflow.StartUrl, UriKind.Absolute, out var start) &&
                            string.Equals(cur.Host, start.Host, StringComparison.OrdinalIgnoreCase);

        if (!alreadyOnSite)
        {
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

        if (workflow.AutoLogin &&
            !string.Equals(workflow.LoginMode, "None", StringComparison.OrdinalIgnoreCase))
        {
            await TryAutoLoginAsync(workflow, page, ct, configuration, keepassMasterPassword)
                .ConfigureAwait(false);
            var health = await LoginHealth.InspectAsync(page, ct).ConfigureAwait(false);
            if (health.NeedsAttention)
            {
                Log($"Login needs attention for '{workflow.Name}': {health.Reason} Pausing further steps for this site.");
                return new LoginAttention(workflow.Id, workflow.Name, health.Reason);
            }
        }

        foreach (var step in workflow.Steps)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ExecuteStepAsync(page, step, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"Step '{step.Type}' failed on '{workflow.Name}': {ex.Message}. Continuing remaining steps when possible.");
            }
        }

        if (!string.IsNullOrWhiteSpace(workflow.DestinationUrl))
        {
            await page.GotoAsync(workflow.DestinationUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            }).ConfigureAwait(false);
        }

        Log($"Finished '{workflow.Name}'.");
        return null;
    }

    private async Task TryAutoLoginAsync(
        WebsiteWorkflow workflow,
        IPage page,
        CancellationToken ct,
        Configuration? configuration,
        string? keepassMasterPassword)
    {
        var credential = _vault.Read(workflow, configuration, keepassMasterPassword);
        if (credential is null)
        {
            Log($"No saved credential for '{workflow.Name}'. Leaving the page open so you can sign in manually.");
            return;
        }

        var origin = GetOrigin(page.Url);
        if (!await IsOriginAllowedAsync(workflow, origin).ConfigureAwait(false))
        {
            Log($"Login origin {origin} is not approved for '{workflow.Name}'. Page left open for manual sign-in.");
            return;
        }

        try
        {
            var filledUser = false;
            var filledPass = false;

            if (!string.IsNullOrWhiteSpace(workflow.UsernameSelector))
            {
                var user = page.Locator(workflow.UsernameSelector).First;
                if (await user.CountAsync().ConfigureAwait(false) > 0)
                {
                    await user.FillAsync(credential.Username).ConfigureAwait(false);
                    filledUser = true;
                    if (!string.IsNullOrWhiteSpace(workflow.UsernameContinueSelector))
                    {
                        await page.Locator(workflow.UsernameContinueSelector).First
                            .ClickAsync().ConfigureAwait(false);
                        await page.WaitForTimeoutAsync(800).ConfigureAwait(false);
                    }
                }
                else
                {
                    Log($"Username field not found for '{workflow.Name}' (selector may be outdated).");
                }
            }

            var afterContinueOrigin = GetOrigin(page.Url);
            // A username step may leave the trusted origin even when no separate password origin was configured.
            if (!await IsOriginAllowedAsync(workflow, afterContinueOrigin).ConfigureAwait(false))
            {
                Log($"Password page origin {afterContinueOrigin} was not approved. Complete sign-in manually if needed.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(workflow.PasswordSelector))
            {
                var pass = page.Locator(workflow.PasswordSelector).First;
                if (await pass.CountAsync().ConfigureAwait(false) > 0)
                {
                    await pass.FillAsync(credential.Password).ConfigureAwait(false);
                    filledPass = true;
                }
                else
                {
                    Log($"Password field not found for '{workflow.Name}' (SSO/MFA page, or selector changed).");
                }
            }

            if (!string.IsNullOrWhiteSpace(workflow.SubmitSelector) && (filledUser || filledPass))
            {
                var submit = page.Locator(workflow.SubmitSelector).First;
                if (await submit.CountAsync().ConfigureAwait(false) > 0)
                    await submit.ClickAsync().ConfigureAwait(false);
            }

            var waitMs = Math.Clamp(workflow.LoginWaitSeconds, 5, 180) * 1000;
            if (!string.IsNullOrWhiteSpace(workflow.LoginSuccessSelector))
            {
                await page.Locator(workflow.LoginSuccessSelector).First
                    .WaitForAsync(new LocatorWaitForOptions { Timeout = waitMs })
                    .ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(workflow.LoginSuccessUrlContains))
            {
                await page.WaitForURLAsync(
                        u => u.Contains(workflow.LoginSuccessUrlContains, StringComparison.OrdinalIgnoreCase),
                        new PageWaitForURLOptions { Timeout = waitMs })
                    .ConfigureAwait(false);
            }
            else
            {
                await page.WaitForTimeoutAsync(Math.Min(waitMs, 3000)).ConfigureAwait(false);
            }

            if (!filledUser && !filledPass)
            {
                Log($"Auto-login could not fill fields for '{workflow.Name}'. " +
                    "Browser stays open — finish MFA/SSO yourself, then teach steps with the visual recorder.");
            }
            else
            {
                Log($"Auto-login attempted for '{workflow.Name}'.");
            }
        }
        catch (Exception ex)
        {
            Log($"Auto-login for '{workflow.Name}' incomplete: {ex.Message}. " +
                "Leaving the browser open for manual sign-in / MFA.");
        }
    }

    private async Task<bool> IsOriginAllowedAsync(WebsiteWorkflow workflow, string origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return false;

        if (!string.IsNullOrWhiteSpace(workflow.CredentialOrigin) &&
            OriginsMatch(origin, GetOrigin(workflow.CredentialOrigin)))
            return true;

        if (!string.IsNullOrWhiteSpace(workflow.PasswordCredentialOrigin) &&
            OriginsMatch(origin, GetOrigin(workflow.PasswordCredentialOrigin)))
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

    private static async Task ExecuteStepAsync(IPage page, WorkflowStep step, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (step.Type)
        {
            case "Navigate":
                ValidateUrl(step.Target);
                await page.GotoAsync(step.Target, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60_000
                }).ConfigureAwait(false);
                break;
            case "Click":
                await page.Locator(step.Target).First.ClickAsync().ConfigureAwait(false);
                break;
            case "Fill":
                await page.Locator(step.Target).First.FillAsync(step.Value ?? "").ConfigureAwait(false);
                break;
            case "Press":
                await page.Keyboard.PressAsync(step.Target).ConfigureAwait(false);
                break;
            case "Wait":
                if (int.TryParse(step.Target, out var ms))
                    await page.WaitForTimeoutAsync(Math.Clamp(ms, 0, 60_000)).ConfigureAwait(false);
                else
                    await page.Locator(step.Target).First.WaitForAsync().ConfigureAwait(false);
                break;
            default:
                // Unknown steps are ignored rather than aborting the run.
                break;
        }
    }

    private async Task<IPage> GetOrCreatePageAsync(WebsiteWorkflow workflow, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_sessions.TryGetValue(workflow.Id, out var existing))
        {
            try
            {
                if (existing.Page.IsClosed)
                    _sessions.Remove(workflow.Id);
                else
                    return existing.Page;
            }
            catch
            {
                _sessions.Remove(workflow.Id);
            }
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
        var channel = ResolveChannel(workflow.Browser);
        if (channel is not null) options.Channel = channel;
        if (!workflow.Browser.Contains("Firefox", StringComparison.OrdinalIgnoreCase))
        {
            options.IgnoreDefaultArgs = new[] { "--enable-automation" };
            options.Args = new[] { "--disable-blink-features=AutomationControlled" };
        }

        var context = await LaunchContextWithRecoveryAsync(browserType, workflow, profileDir, options, ct).ConfigureAwait(false);
        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync().ConfigureAwait(false);
        _sessions[workflow.Id] = new TrackedSession(context, page);
        return page;
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

    private async Task PositionWindowAsync(IPage page, int monitorIndex)
    {
        try
        {
            var monitors = MonitorService.GetMonitors();
            var monitor = monitors[Math.Clamp(monitorIndex, 0, monitors.Count - 1)];
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
                    ["left"] = bounds.Left + 8,
                    ["top"] = bounds.Top + 8,
                    ["width"] = Math.Max(800, bounds.Width - 16),
                    ["height"] = Math.Max(600, bounds.Height - 16),
                    ["windowState"] = "normal"
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("Could not position browser on monitor: " + ex.Message);
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

    private static string GetOrigin(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
        return $"{uri.Scheme}://{uri.Authority}";
    }

    private static bool OriginsMatch(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private void Log(string message) => OnLog?.Invoke(message);

    private sealed record TrackedSession(IBrowserContext Context, IPage Page);
}
