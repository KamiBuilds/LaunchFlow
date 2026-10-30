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
    // Explicit user acknowledgment applies only to this exact browser page and
    // URL, never to a new login, navigation, restored profile, or future launch.
    private sealed class LoginAcknowledgment(IPage page, string url)
    {
        public IPage Page { get; } = page;
        public string Url { get; } = url;
        public bool Navigated { get; set; } // Read and written under _gate.
    }

    private readonly Dictionary<Guid, LoginAcknowledgment> _confirmedLogins = new();
    private readonly Dictionary<Guid, (IPage Page, EventHandler<IFrame> Handler)> _confirmationNavigationHooks = new();
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

    /// <summary>
    /// Called only after the user explicitly clicks "I'm signed in" while
    /// looking at their browser. Does not edit credentials or trust future
    /// sessions. An unapproved site or visible MFA/password challenge is
    /// never accepted as a completed login.
    /// </summary>
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
            lock (_gate)
            {
                _confirmedLogins.Clear();
                foreach (var hook in _confirmationNavigationHooks.Values)
                    hook.Page.FrameNavigated -= hook.Handler;
                _confirmationNavigationHooks.Clear();
            }
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

    private static string GetOrigin(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
        return $"{uri.Scheme}://{uri.Authority}";
    }

    private static bool OriginsMatch(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private void Log(string message) => OnLog?.Invoke(message);
    private void Status(WebsiteWorkflow workflow, WorkflowRunState state) => OnWorkflowStatus?.Invoke(workflow.Name, state);

    private static string PublicStepType(string type) => type is "Navigate" or "Click" or "Fill" or "Press" or "Wait" or "AssertVisible" or "WaitForURL"
        ? type : "Unsupported";

    private sealed record TrackedSession(IBrowserContext Context, IPage Page);
}
