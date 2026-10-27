using System.Reflection;
using System.Runtime.ExceptionServices;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;

internal static class EngineLifecycleTests
{
    public static async Task RunAsync(IPlaywright playwright, string[] args)
    {
        if (args.Contains("--document-bound-only") || args.Contains("--profile-only")) return;
        if (args.Contains("--teach-launch-only")) { await LaunchRaceAsync(playwright, teach: true); return; }
        await ClosedPageOwnershipAsync(playwright);
        await CloseFromRunningStatusAsync();
        await CloseActiveRunAsync(playwright);
        await LaunchRaceAsync(playwright, teach: false);
        await LaunchRaceAsync(playwright, teach: true);
        await LaunchRaceAsync(playwright, teach: false, cleanupFailure: true);
    }

    private static async Task ClosedPageOwnershipAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.CloseAsync();
        await using var engine = new AutomationEngine(new CredentialVault());
        var workflow = new WebsiteWorkflow { Browser = "Chromium" };
        EngineBehaviorTests.Track(engine, workflow, context, page);
        // Inert launch boundary aborts replacement before touching disk or starting a headful browser.
        var runtime = ForwardProxy<IPlaywright>.Create(playwright, (method, _) =>
        {
            if (method.Name == "get_Chromium") throw new InvalidOperationException("inert launch boundary");
            return method.Name == nameof(IDisposable.Dispose) ? (true, null) : (false, null);
        });
        typeof(AutomationEngine).GetField("_playwright", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(engine, runtime);
        await RejectedAsync(() => engine.GetPageForRecorderAsync(workflow));
        EngineBehaviorTests.Check(!browser.Contexts.Contains(context) && SessionCount(engine) == 0,
            "a closed tracked page cannot orphan its still-live browser context when replacing the session");
    }

    private static async Task CloseFromRunningStatusAsync()
    {
        await using var engine = new AutomationEngine(new CredentialVault());
        Task? close = null;
        var returnedBeforeRunFinished = false;
        engine.OnWorkflowStatus += (_, state) =>
        {
            if (state != AutomationEngine.WorkflowRunState.Running) return;
            close = engine.CloseSessionsAsync();
            returnedBeforeRunFinished = close.IsCompleted;
        };
        var workflow = new WebsiteWorkflow { StartUrl = "invalid-inert-no-launch", AutoLogin = false };
        var run = engine.RunAsync(new[] { workflow }, CancellationToken.None);
        var cancelled = await CancelledAsync(run);
        await close!;
        EngineBehaviorTests.Check(cancelled && !returnedBeforeRunFinished && run.IsCompleted,
            "a Running-status close cannot finish before its newly admitted run task is registered and finished");
    }

    private static async Task LaunchRaceAsync(IPlaywright playwright, bool teach, bool cleanupFailure = false)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        await context.RouteAsync("**/*", r => r.FulfillAsync(new() { ContentType = "text/html", Body = "<h1>Ready</h1>" }));
        var page = await context.NewPageAsync();
        var launched = Signal();
        var releaseLaunch = Signal();
        var closed = false;
        var pagesReads = 0;
        var claimedAtClose = false;
        var rejectClose = cleanupFailure;
        await using var engine = new AutomationEngine(new CredentialVault());
        // Explicit inert launch seam: defer profile launch return, then return a real browser context.
        // No native/headful persistent browser or real vault data is used in this timing test.
        var returnedContext = ForwardProxy<IBrowserContext>.Create(context, (method, _) =>
        {
            if (method.Name == "get_Pages") Interlocked.Increment(ref pagesReads);
            if (method.Name != nameof(IBrowserContext.CloseAsync)) return (false, null);
            claimedAtClose |= SessionCount(engine) != 0;
            if (rejectClose) return (true, Task.FromException(new InvalidOperationException("private-close-diagnostic")));
            return (true, CloseReturnedAsync());
        });
        async Task CloseReturnedAsync() { await context.CloseAsync(); closed = true; }
        async Task<IBrowserContext> LaunchAsync()
        {
            launched.TrySetResult();
            await releaseLaunch.Task;
            return returnedContext;
        }
        var browserType = ForwardProxy<IBrowserType>.Create(playwright.Chromium, (method, _) =>
            method.Name == nameof(IBrowserType.LaunchPersistentContextAsync) ? (true, LaunchAsync()) : (false, null));
        var runtime = ForwardProxy<IPlaywright>.Create(playwright, (method, _) => method.Name switch
        {
            "get_Chromium" => (true, browserType),
            nameof(IDisposable.Dispose) => (true, null),
            _ => (false, null)
        });
        typeof(AutomationEngine).GetField("_playwright", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(engine, runtime);
        var oldXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var root = Path.Combine(Path.GetTempPath(), "launchflow-launch-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", root);
        try
        {
            if (!OperatingSystem.IsLinux() || Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) != root)
                throw new Exception("HARNESS: isolated profile timing seam requires Linux XDG_DATA_HOME");
            var workflow = new WebsiteWorkflow { StartUrl = "https://launch.test/", Browser = "Chromium", AutoLogin = false };
            Task operation = teach ? engine.GetPageForRecorderAsync(workflow) : engine.RunAsync(new[] { workflow }, CancellationToken.None);
            await launched.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var close = engine.CloseSessionsAsync();
            var blockedUntilLaunch = !close.IsCompleted;
            var rejected = await RejectedAsync(() => engine.RunAsync(Array.Empty<WebsiteWorkflow>(), CancellationToken.None)) &&
                await RejectedAsync(() => engine.GetPageForRecorderAsync(workflow));
            releaseLaunch.TrySetResult();
            var stopped = teach ? await RejectedAsync(() => operation) : await CancelledAsync(operation);
            if (cleanupFailure)
            {
                var failedSafely = false;
                try { await close.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (InvalidOperationException ex) { failedSafely = !ex.Message.Contains("private-close-diagnostic", StringComparison.Ordinal); }
                var blockedAfterFailure = await RejectedAsync(() => engine.RunAsync(Array.Empty<WebsiteWorkflow>(), CancellationToken.None)) &&
                    await RejectedAsync(() => engine.GetPageForRecorderAsync(workflow));
                rejectClose = false;
                await engine.CloseSessionsAsync();
                EngineBehaviorTests.Check(failedSafely && blockedAfterFailure && closed,
                    "unclaimed-context close failure fails closed, retains ownership for retry and never echoes raw diagnostics");
            }
            else await close.WaitAsync(TimeSpan.FromSeconds(15));
            EngineBehaviorTests.Check(blockedUntilLaunch && rejected && stopped, (teach ? "Teach" : "run") + " launch is excluded while closing and close waits for launch ownership");
            EngineBehaviorTests.Check(closed && page.IsClosed && SessionCount(engine) == 0 && pagesReads == 0 && !claimedAtClose,
                (teach ? "Teach" : "run") + " cancelled launch closes its unclaimed context without inspecting pages or repopulating sessions");
        }
        finally
        {
            releaseLaunch.TrySetResult();
            rejectClose = false;
            try { await engine.CloseSessionsAsync(); } catch (InvalidOperationException) { }
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldXdg);
            Directory.Delete(root, true);
        }
    }

    private static async Task CloseActiveRunAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        await context.RouteAsync("**/*", r => r.FulfillAsync(new() { ContentType = "text/html", Body = "<button id='begun' onclick=\"document.body.dataset.begun='yes'\">begin</button><button id='later'>later</button>" }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://close.test/");
        var closeEntered = Signal();
        var releaseClose = Signal();
        var contextClosed = Signal();
        var closeCalls = 0;
        // Explicit inert context-close timing seam; all workflow actions still use a real routed Playwright page.
        var ownedContext = ForwardProxy<IBrowserContext>.Create(context, (method, _) =>
            method.Name == nameof(IBrowserContext.CloseAsync) ? (true, DelayedCloseAsync()) : (false, null));
        async Task DelayedCloseAsync()
        {
            Interlocked.Increment(ref closeCalls);
            closeEntered.TrySetResult();
            await releaseClose.Task;
            await context.CloseAsync();
            contextClosed.TrySetResult();
        }
        var workflow = new WebsiteWorkflow { StartUrl = page.Url, AutoLogin = false, DestinationUrl = "https://close.test/destination" };
        workflow.Steps.Add(new() { Type = "Click", Target = "#begun" });
        workflow.Steps.Add(new() { Type = "Wait", Target = "60000" });
        workflow.Steps.Add(new() { Type = "Click", Target = "#later" });
        await using var engine = new AutomationEngine(new CredentialVault());
        EngineBehaviorTests.Track(engine, workflow, ownedContext, page);
        var states = new List<AutomationEngine.WorkflowRunState>();
        var runUnwinding = Signal();
        var releaseRun = Signal();
        engine.OnWorkflowStatus += (_, state) =>
        {
            states.Add(state);
            if (state != AutomationEngine.WorkflowRunState.Cancelled) return;
            // Inert synchronous UI subscriber deliberately holds run unwinding after cancellation.
            runUnwinding.TrySetResult();
            releaseRun.Task.GetAwaiter().GetResult();
        };
        try
        {
            var run = engine.RunAsync(new[] { workflow }, CancellationToken.None);
            await page.WaitForFunctionAsync("document.body.dataset.begun === 'yes'");
            var close = engine.CloseSessionsAsync();
            await closeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var concurrentClose = engine.CloseSessionsAsync();
            var rejectedRun = await RejectedAsync(() => engine.RunAsync(Array.Empty<WebsiteWorkflow>(), CancellationToken.None));
            var rejectedTeach = await RejectedAsync(() => engine.GetPageForRecorderAsync(workflow));
            releaseClose.TrySetResult();
            await contextClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await runUnwinding.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var waitsForUnwinding = !close.IsCompleted && engine.IsRunning;
            releaseRun.TrySetResult();
            await Task.WhenAll(close, concurrentClose).WaitAsync(TimeSpan.FromSeconds(15));
            var cancelled = await CancelledAsync(run);
            EngineBehaviorTests.Check(rejectedRun && rejectedTeach, "closing rejects new runs and Teach access even while context close is pending");
            EngineBehaviorTests.Check(closeCalls == 1, "concurrent CloseSessions callers join one context lifecycle writer");
            EngineBehaviorTests.Check(cancelled && states.SequenceEqual(new[] { AutomationEngine.WorkflowRunState.Running, AutomationEngine.WorkflowRunState.Cancelled }),
                "CloseSessions cancels its active run and emits Cancelled rather than Failed or Completed");
            EngineBehaviorTests.Check(contextClosed.Task.IsCompleted && run.IsCompleted && !engine.IsRunning && SessionCount(engine) == 0,
                "CloseSessions returns only after owned contexts and active run are finished");
            EngineBehaviorTests.Check(waitsForUnwinding, "owned contexts close before awaiting active-run unwinding and admission stays blocked until completion");
            var restarted = await engine.RunAsync(Array.Empty<WebsiteWorkflow>(), CancellationToken.None);
            EngineBehaviorTests.Check(restarted.FinishedCount == 0, "run admission resumes after the close barrier has completed");
        }
        finally
        {
            releaseClose.TrySetResult();
            releaseRun.TrySetResult();
        }
    }

    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static int SessionCount(AutomationEngine engine) => ((System.Collections.IDictionary)typeof(AutomationEngine)
        .GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!).Count;
    internal static async Task<bool> RejectedAsync(Func<Task> action)
    {
        try { await action(); return false; }
        catch (InvalidOperationException) { return true; }
    }
    internal static async Task<bool> CancelledAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(15)); return false; }
        catch (OperationCanceledException) { return true; }
    }
}

// Test-only interface seam: unhandled members are forwarded to the real Playwright object.
internal class ForwardProxy<T> : DispatchProxy where T : class
{
    private T _target = null!;
    private Func<MethodInfo, object?[]?, (bool Handled, object? Result)> _handler = null!;
    internal static T Create(T target, Func<MethodInfo, object?[]?, (bool, object?)> handler)
    {
        var proxy = Create<T, ForwardProxy<T>>();
        var state = (ForwardProxy<T>)(object)proxy;
        state._target = target;
        state._handler = handler;
        return proxy;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var result = _handler(method!, args);
        if (result.Handled) return result.Result;
        try { return method!.Invoke(_target, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
