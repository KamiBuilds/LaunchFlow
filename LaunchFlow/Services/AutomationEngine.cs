using System.IO;
using LaunchFlow.Models;
using Microsoft.Playwright;

namespace LaunchFlow.Services;

public sealed partial class AutomationEngine : IAsyncDisposable
{
    private readonly CredentialVault _vault;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly Dictionary<Guid, TrackedSession> _sessions = new();
    private readonly HashSet<IBrowserContext> _unclaimedContexts = new();
    private IPlaywright? _playwright;
    private bool _running;
    private bool _closing;
    private bool _cleanupRequired;
    private bool _disposed;
    private CancellationTokenSource? _activeRunCancellation;
    private Task _activeRunTask = Task.CompletedTask;
    private Task? _closeTask;

    public event Action<string>? OnLog;
    public enum WorkflowRunState { Running, WaitingForSignIn, Completed, Failed, Cancelled }
    public event Action<string, WorkflowRunState>? OnWorkflowStatus;
    public Func<WebsiteWorkflow, string, Task<bool>>? ApproveLoginOriginAsync { get; set; }

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public sealed record LoginAttention(Guid WorkflowId, string WorkflowName, string Reason);
    public sealed record StepAttention(Guid WorkflowId, string WorkflowName, string StepType);
    private sealed class WorkflowStepException(string stepType) : Exception("Workflow step needs repair.")
    {
        public string StepType { get; } = stepType;
    }

    public sealed class RunResult
    {
        public List<LoginAttention> LoginNeedsAttention { get; } = new();
        public List<StepAttention> StepsNeedAttention { get; } = new();
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
            throw new ArgumentException("A valid HTTP or HTTPS URL is required.");
    }

    private static void RequireAutomationUrl(string url)
    {
        var policy = EnterprisePolicy.Current;
        if (policy.DisableAutomation || !policy.IsUrlAllowed(url))
            throw new InvalidOperationException("This website is blocked by your organization's policy.");
    }

    private static void RequireCredentialAccess()
    {
        var policy = EnterprisePolicy.Current;
        if (policy.DisableAutomation)
            throw new InvalidOperationException("Automation is disabled by your organization's policy.");
        policy.RequireCredentialRead();
    }

    private static void RequireTeachUrl(string url)
    {
        var policy = EnterprisePolicy.Current;
        if (policy.DisableAutomation || policy.DisableTeachMode || !policy.IsUrlAllowed(url))
            throw new InvalidOperationException("Teach is blocked by your organization's policy.");
    }

    public Task<RunResult> RunAsync(
        IEnumerable<WebsiteWorkflow> workflows,
        CancellationToken cancellationToken,
        bool includeDisabled = false,
        Configuration? configuration = null,
        string? keepassMasterPassword = null)
    {
        if (EnterprisePolicy.Current.DisableAutomation)
            throw new InvalidOperationException("Automation is disabled by your organization's policy.");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<RunResult> task;
        lock (_gate)
        {
            CheckSessionAdmission();
            if (_running)
                throw new InvalidOperationException("Automation is already running.");
            _running = true;
            var linkedRun = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeRunCancellation = linkedRun;
            task = RunCoreAsync(start.Task, workflows, linkedRun, includeDisabled, configuration, keepassMasterPassword);
            _activeRunTask = task;
        }
        // Register ownership before status subscribers or synchronous validation can initiate a close.
        start.SetResult();
        return task;
    }

    private async Task<RunResult> RunCoreAsync(
        Task start, IEnumerable<WebsiteWorkflow> workflows, CancellationTokenSource linkedRun, bool includeDisabled,
        Configuration? configuration, string? keepassMasterPassword)
    {
        await start.ConfigureAwait(false);
        var cancellationToken = linkedRun.Token;
        var result = new RunResult();
        try
        {
            foreach (var workflow in workflows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!includeDisabled && !workflow.Enabled) continue;
                Status(workflow, WorkflowRunState.Running);
                try
                {
                    var attention = await RunOneAsync(workflow, cancellationToken, configuration, keepassMasterPassword)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (attention is not null)
                    {
                        result.LoginNeedsAttention.Add(attention);
                        Status(workflow, WorkflowRunState.WaitingForSignIn);
                    }
                    else
                    {
                        result.FinishedCount++;
                        Status(workflow, WorkflowRunState.Completed);
                    }
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    Status(workflow, WorkflowRunState.Cancelled);
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (WorkflowStepException ex)
                {
                    result.FailedCount++;
                    result.StepsNeedAttention.Add(new StepAttention(workflow.Id, workflow.Name, ex.StepType));
                    Status(workflow, WorkflowRunState.Failed);
                    Log($"Workflow '{workflow.Name}' needs a step repaired: {ex.StepType}.");
                }
                catch (Exception)
                {
                    result.FailedCount++;
                    Status(workflow, WorkflowRunState.Failed);
                    Log($"Workflow '{workflow.Name}' failed. Check or repair the workflow.");
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
                _activeRunCancellation = null;
            }
            linkedRun.Dispose();
        }

        return result;
    }

    public async Task<IPage> GetPageForRecorderAsync(WebsiteWorkflow workflow)
    {
        RequireTeachUrl(workflow.StartUrl);
        lock (_gate) CheckSessionAdmission();
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate) CheckSessionAdmission();
            ValidateUrl(workflow.StartUrl);
            RequireTeachUrl(workflow.StartUrl);
            await EnsurePlaywrightAsync().ConfigureAwait(false);
            var page = await GetOrCreatePageCoreAsync(workflow, CancellationToken.None).ConfigureAwait(false);
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
            RequireTeachUrl(page.Url);
            lock (_gate) CheckSessionAdmission();
            return page;
        }
        finally { _sessionGate.Release(); }
    }

    /// <summary>
    /// Close browser contexts so profile folders can be sealed on vault lock.
    /// Playwright itself stays alive for the next unlock.
    /// </summary>
    public Task CloseSessionsAsync()
    {
        TaskCompletionSource completion;
        CancellationTokenSource? active;
        Task activeRun;
        lock (_gate)
        {
            if (_closeTask is not null) return _closeTask;
            _closing = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _closeTask = completion.Task;
            active = _activeRunCancellation;
            activeRun = _activeRunTask;
        }
        _ = CloseSessionsCoreAsync(completion, active, activeRun);
        return completion.Task;
    }

    private async Task CloseSessionsCoreAsync(TaskCompletionSource completion, CancellationTokenSource? active, Task activeRun)
    {
        var failed = false;
        try
        {
            Task cancellation = Task.CompletedTask;
            try { if (active is not null) cancellation = active.CancelAsync(); }
            catch (ObjectDisposedException) { /* The run has already finished. */ }
            await _sessionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var entry in _sessions.ToArray())
                {
                    try
                    {
                        await entry.Value.Context.CloseAsync().ConfigureAwait(false);
                        _sessions.Remove(entry.Key);
                    }
                    catch { failed = true; }
                }
                foreach (var context in _unclaimedContexts.ToArray())
                {
                    try
                    {
                        await context.CloseAsync().ConfigureAwait(false);
                        _unclaimedContexts.Remove(context);
                    }
                    catch { failed = true; }
                }
            }
            finally { _sessionGate.Release(); }
            // Closing contexts first releases dispatched browser actions; never await run termination before closing them.
            try { await cancellation.ConfigureAwait(false); } catch { failed = true; }
            try { await activeRun.ConfigureAwait(false); } catch { /* The run's caller owns its result/cancellation. */ }
        }
        catch { failed = true; }
        finally
        {
            lock (_gate)
            {
                _closing = false;
                _cleanupRequired = failed;
                _closeTask = null;
                if (failed) completion.TrySetException(new InvalidOperationException("Browser sessions could not be closed. Keep the vault unlocked and retry."));
                else completion.TrySetResult();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        await CloseSessionsAsync().ConfigureAwait(false);
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _playwright?.Dispose();
            _playwright = null;
        }
        finally { _sessionGate.Release(); }
    }

    // Call only while holding _gate. The async session gate owns Playwright creation, session writes and closing.
    private void CheckSessionAdmission()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_closing) throw new InvalidOperationException("Browser sessions are closing. Try again after closing finishes.");
        if (_cleanupRequired) throw new InvalidOperationException("Browser cleanup is incomplete. Retry closing sessions before continuing.");
    }

    private async Task<LoginAttention?> RunOneAsync(
        WebsiteWorkflow workflow,
        CancellationToken ct,
        Configuration? configuration,
        string? keepassMasterPassword)
    {
        ct.ThrowIfCancellationRequested();
        Log($"Starting '{workflow.Name}'…");
        ValidateUrl(workflow.StartUrl);
        RequireAutomationUrl(workflow.StartUrl);
        if (!string.IsNullOrWhiteSpace(workflow.DestinationUrl))
        {
            ValidateUrl(workflow.DestinationUrl);
            RequireAutomationUrl(workflow.DestinationUrl);
        }

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
        await PositionWindowAsync(page, workflow).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var currentUrl = page.Url;
        var alreadyOnSite = !string.IsNullOrEmpty(currentUrl) &&
                            Uri.TryCreate(currentUrl, UriKind.Absolute, out var cur) &&
                            Uri.TryCreate(workflow.StartUrl, UriKind.Absolute, out var start) &&
                            string.Equals(cur.Scheme, start.Scheme, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(cur.Authority, start.Authority, StringComparison.OrdinalIgnoreCase);

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

        ct.ThrowIfCancellationRequested();
        RequireAutomationUrl(page.Url);
        if (workflow.AutoLogin &&
            !string.Equals(workflow.LoginMode, "None", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(workflow.LoginMode, "SSO", StringComparison.OrdinalIgnoreCase) &&
                !await TryAutoLoginAsync(workflow, page, ct, configuration, keepassMasterPassword)
                    .ConfigureAwait(false))
                return new LoginAttention(workflow.Id, workflow.Name,
                    "Auto-login was interrupted or its credential field was not approved/ready. Retry sign-in manually before continuing.");
            var health = await LoginHealth.InspectAsync(page, ct).ConfigureAwait(false);
            if (health.NeedsAttention)
            {
                Log($"Login needs attention for '{workflow.Name}': {health.Reason} Pausing further steps for this site.");
                return new LoginAttention(workflow.Id, workflow.Name, health.Reason);
            }
            if (workflow.Steps.Count > 0 || !string.IsNullOrWhiteSpace(workflow.DestinationUrl))
            {
                var verified = await LoginHealth.VerifySuccessAsync(
                    page, workflow.LoginSuccessSelector, workflow.LoginSuccessUrlContains, ct)
                    .ConfigureAwait(false);
                if (verified.NeedsAttention)
                {
                    Log($"Login not verified for '{workflow.Name}'. Follow-up actions were paused.");
                    return new LoginAttention(workflow.Id, workflow.Name, verified.Reason);
                }
            }
        }

        foreach (var step in workflow.Steps)
        {
            ct.ThrowIfCancellationRequested();
            // A failed action leaves browser state uncertain; do not dispatch later steps or a destination navigation.
            try { await ExecuteStepAsync(page, step, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw new WorkflowStepException(PublicStepType(step.Type)); }
            ct.ThrowIfCancellationRequested();
        }

        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(workflow.DestinationUrl))
        {
            RequireAutomationUrl(workflow.DestinationUrl);
            await page.GotoAsync(workflow.DestinationUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            }).ConfigureAwait(false);
        }

        RequireAutomationUrl(page.Url);
        Log($"Finished '{workflow.Name}'.");
        return null;
    }

    private async Task<bool> TryAutoLoginAsync(
        WebsiteWorkflow workflow,
        IPage page,
        CancellationToken ct,
        Configuration? configuration,
        string? keepassMasterPassword)
    {
        ct.ThrowIfCancellationRequested();
        RequireCredentialAccess();
        var credential = _vault.Read(workflow, configuration, keepassMasterPassword);
        if (credential is null)
        {
            Log($"No saved credential for '{workflow.Name}'. Leaving the page open so you can sign in manually.");
            return true;
        }

        try
        {
            var filledUser = false;
            var filledPass = false;

            if (!string.IsNullOrWhiteSpace(workflow.UsernameSelector))
            {
                if (await FillCredentialAsync(workflow, page, workflow.UsernameSelector, credential.Username, ct).ConfigureAwait(false))
                {
                    filledUser = true;
                    if (!string.IsNullOrWhiteSpace(workflow.UsernameContinueSelector))
                    {
                        ct.ThrowIfCancellationRequested();
                        await page.Locator(workflow.UsernameContinueSelector).First
                            .ClickAsync().ConfigureAwait(false);
                        await Task.Delay(800, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    Log($"Username field not found for '{workflow.Name}' (selector may be outdated).");
                }
            }

            if (!string.IsNullOrWhiteSpace(workflow.PasswordSelector))
            {
                if (await FillCredentialAsync(workflow, page, workflow.PasswordSelector, credential.Password, ct).ConfigureAwait(false))
                {
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
                {
                    ct.ThrowIfCancellationRequested();
                    await submit.ClickAsync().ConfigureAwait(false);
                }
            }

            ct.ThrowIfCancellationRequested();
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
                await Task.Delay(Math.Min(waitMs, 3000), ct).ConfigureAwait(false);
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
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            Log($"Auto-login for '{workflow.Name}' incomplete. " +
                "Leaving the browser open for manual sign-in / MFA.");
            return false;
        }
    }

    private async Task<bool> FillCredentialAsync(
        WebsiteWorkflow workflow, IPage page, string selector, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // A locator can re-resolve in another document during auto-wait. Approval belongs only to this handle.
        await using var element = await page.QuerySelectorAsync(selector).ConfigureAwait(false);
        if (element is null) return false;
        var frame = await element.OwnerFrameAsync().ConfigureAwait(false);
        if (frame is null) throw new InvalidOperationException("Credential field has no owning frame. Retry sign-in manually.");
        var navigated = 0;
        void Invalidate(object? sender, IFrame changed)
        {
            if (ReferenceEquals(changed, frame)) Interlocked.Exchange(ref navigated, 1);
        }
        page.FrameNavigated += Invalidate;
        page.FrameDetached += Invalidate;
        try
        {
            await using var document = await element.EvaluateHandleAsync("element => element.ownerDocument").ConfigureAwait(false);
            async Task<string> VerifyAsync()
            {
                ct.ThrowIfCancellationRequested();
                if (Volatile.Read(ref navigated) != 0 || frame.IsDetached ||
                    !ReferenceEquals(await element.OwnerFrameAsync().ConfigureAwait(false), frame))
                    throw new InvalidOperationException("Credential document changed. Retry sign-in manually.");
                var origin = await element.EvaluateAsync<string>("""
                    (element, document) => {
                        if (!element.isConnected || element.ownerDocument !== document ||
                            document.defaultView?.document !== document)
                            throw new Error('Credential document changed');
                        return document.location.origin;
                    }
                    """, document).ConfigureAwait(false);
                ValidateUrl(origin);
                RequireCredentialAccess();
                RequireAutomationUrl(origin);
                if (new Uri(origin).Scheme != Uri.UriSchemeHttps)
                    throw new InvalidOperationException("Automatic credentials require HTTPS. Use manual sign-in for HTTP sites.");
                if (Volatile.Read(ref navigated) != 0)
                    throw new InvalidOperationException("Credential document changed. Retry sign-in manually.");
                return origin;
            }

            var origin = await VerifyAsync().ConfigureAwait(false);
            await element.WaitForElementStateAsync(ElementState.Visible).WaitAsync(ct).ConfigureAwait(false);
            await element.WaitForElementStateAsync(ElementState.Editable).WaitAsync(ct).ConfigureAwait(false);
            if (!OriginsMatch(origin, await VerifyAsync().ConfigureAwait(false)))
                throw new InvalidOperationException("Credential origin changed. Retry sign-in manually.");
            if (!await IsOriginAllowedAsync(workflow, origin).WaitAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("Credential origin was not approved. Complete sign-in manually.");
            // Approval can await a dialog while the page navigates or the bound field becomes unavailable.
            await element.WaitForElementStateAsync(ElementState.Visible).WaitAsync(ct).ConfigureAwait(false);
            await element.WaitForElementStateAsync(ElementState.Editable).WaitAsync(ct).ConfigureAwait(false);
            if (!OriginsMatch(origin, await VerifyAsync().ConfigureAwait(false)))
                throw new InvalidOperationException("Credential origin changed. Retry sign-in manually.");
            ct.ThrowIfCancellationRequested();
            await element.FillAsync(value).ConfigureAwait(false);
            return true;
        }
        catch (PlaywrightException)
        {
            // Playwright fill diagnostics can contain the value; only a non-secret retry reason may leave this boundary.
            throw new InvalidOperationException("Credential field changed or was not ready. Retry sign-in manually.");
        }
        finally
        {
            page.FrameNavigated -= Invalidate;
            page.FrameDetached -= Invalidate;
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
                await page.Keyboard.PressAsync(step.Target).ConfigureAwait(false);
                break;
            case "Wait":
                if (int.TryParse(step.Target, out var ms))
                    await Task.Delay(Math.Clamp(ms, 0, 60_000), ct).ConfigureAwait(false);
                else
                    await page.Locator(step.Target).First.WaitForAsync().ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException("Unsupported workflow step.");
        }
        RequireAutomationUrl(page.Url);
    }

    private static async Task ExecuteElementActionAsync(IPage page, WorkflowStep step, CancellationToken ct)
    {
        // Resolve once, then authorize the document owning that element. Navigation cannot retarget this handle.
        await using var element = await page.Locator(step.Target).First.ElementHandleAsync().ConfigureAwait(false);
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
            await element.FillAsync(step.Value ?? "").ConfigureAwait(false);
        }
        else await element.ClickAsync().ConfigureAwait(false);
    }

    private static string GetOrigin(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
        return $"{uri.Scheme}://{uri.Authority}";
    }

    private static bool OriginsMatch(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private void Log(string message) => OnLog?.Invoke(message);
    private void Status(WebsiteWorkflow workflow, WorkflowRunState state) => OnWorkflowStatus?.Invoke(workflow.Name, state);

    private static string PublicStepType(string type) => type is "Navigate" or "Click" or "Fill" or "Press" or "Wait"
        ? type : "Unsupported";

    private sealed record TrackedSession(IBrowserContext Context, IPage Page);
}
