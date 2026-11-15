using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;

internal static class DispatchedFillTests
{
    public static async Task RunAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(5000);
        await context.RouteAsync("**/*", r => r.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = "<button id='begin' onclick=\"document.body.dataset.begun='yes'\">begin</button><input id='blocked' disabled><button id='later' onclick=\"document.body.dataset.followed='yes'\">later</button>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://dispatch.test/");
        var workflow = new WebsiteWorkflow { StartUrl = page.Url, AutoLogin = false, DestinationUrl = "https://dispatch.test/destination" };
        workflow.Steps.Add(new() { Type = "Click", Target = "#begin" });
        workflow.Steps.Add(new() { Type = "Fill", Target = "#blocked", Value = "synthetic-private-fill" });
        workflow.Steps.Add(new() { Type = "Click", Target = "#later" });
        await using var engine = new AutomationEngine(new CredentialVault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        using var cancellation = new CancellationTokenSource();
        var states = new List<AutomationEngine.WorkflowRunState>();
        engine.OnWorkflowStatus += (_, state) => states.Add(state);
        var destination = false;
        page.Request += (_, request) => destination |= request.Url.EndsWith("/destination", StringComparison.Ordinal);
        var run = engine.RunAsync(new[] { workflow }, cancellation.Token);
        await page.WaitForFunctionAsync("document.body.dataset.begun === 'yes'");
        // Bounded real-browser scheduling fixture, not an atomic-dispatch guarantee. A retained WaitAsync mutation must fail it.
        await Task.Delay(250);
        cancellation.Cancel();
        await Task.Delay(150);
        var stillOwned = !run.IsCompleted;
        await page.EvaluateAsync("document.querySelector('#blocked').disabled = false");
        var cancelled = await EngineLifecycleTests.CancelledAsync(run);
        EngineBehaviorTests.Check(stillOwned && cancelled && await page.InputValueAsync("#blocked") == "synthetic-private-fill",
            "caller cancellation waits for an already-dispatched Fill instead of abandoning secret-bearing browser work");
        EngineBehaviorTests.Check(!destination && await page.GetAttributeAsync("body", "data-followed") is null &&
            states.SequenceEqual(new[] { AutomationEngine.WorkflowRunState.Running, AutomationEngine.WorkflowRunState.Cancelled }),
            "cancellation after dispatched fill prevents later action and destination and emits Cancelled");
    }
}
