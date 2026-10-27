using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;

internal static class WorkflowExecutionTests
{
    public static async Task RunAsync(IPlaywright playwright, string[] args)
    {
        if (args.Contains("--document-bound-only") || args.Contains("--profile-only")) return;
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(2500);
        context.SetDefaultNavigationTimeout(15_000);
        await context.RouteAsync("**/*", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = "<button id='followup' onclick=\"document.body.dataset.followed='yes'\">follow up</button>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://execution.test/");
        var workflow = new WebsiteWorkflow { StartUrl = page.Url, DestinationUrl = "https://execution.test/destination", AutoLogin = false };
        var destinationVisited = false;
        page.Request += (_, request) => destinationVisited |= request.Url.EndsWith("/destination", StringComparison.Ordinal);
        workflow.Steps.Add(new() { Type = "Click", Target = "#missing" });
        workflow.Steps.Add(new() { Type = "Click", Target = "#followup" });
        await using var engine = new AutomationEngine(new CredentialVault());
        var states = new List<(string Name, AutomationEngine.WorkflowRunState State)>();
        engine.OnWorkflowStatus += (name, state) => states.Add((name, state));
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(await page.GetAttributeAsync("body", "data-followed") is null,
            "a failed step blocks later workflow actions");
        Check(result.FailedCount == 1 && result.FinishedCount == 0,
            "a failed step is not reported as a completed workflow");
        Check(!destinationVisited, "failed step prevents destination navigation");
        var attention = result.StepsNeedAttention.Single();
        Check(attention.WorkflowId == workflow.Id && attention.WorkflowName == workflow.Name && attention.StepType == "Click",
            "structured step attention identifies workflow and fixed action type without selector data");
        Check(states.Select(s => s.State).SequenceEqual(new[] { AutomationEngine.WorkflowRunState.Running, AutomationEngine.WorkflowRunState.Failed }),
            "failed action emits Running then Failed");
        states.Clear();
        workflow.Steps.RemoveAt(0);
        workflow.DestinationUrl = "";
        var completed = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(completed.FinishedCount == 1 && states.All(s => s.Name == workflow.Name) &&
            states.Select(s => s.State).SequenceEqual(new[] { AutomationEngine.WorkflowRunState.Running, AutomationEngine.WorkflowRunState.Completed }),
            "successful workflow emits named Running then Completed");
        states.Clear();
        var logs = new List<string>();
        engine.OnLog += logs.Add;
        await page.EvaluateAsync("delete document.body.dataset.followed");
        workflow.DestinationUrl = "https://execution.test/destination";
        workflow.Steps.Insert(0, new() { Type = "private-unsupported-action", Target = "private-query", Value = "private-fill" });
        var unsupported = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(unsupported.FailedCount == 1 && unsupported.FinishedCount == 0 &&
            unsupported.StepsNeedAttention.Single().StepType == "Unsupported", "unsupported step fails with bounded public type");
        Check(!destinationVisited && await page.GetAttributeAsync("body", "data-followed") is null,
            "unsupported step prevents later actions and destination navigation");
        Check(!logs.Any(log => log.Contains("private-", StringComparison.Ordinal)), "unsupported diagnostics do not echo private step data");

        workflow.Steps.Clear();
        workflow.DestinationUrl = "";
        workflow.Steps.Add(new() { Type = "AssertVisible", Target = "#followup" });
        workflow.Steps.Add(new() { Type = "WaitForURL", Target = page.Url });
        var verified = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(verified.FinishedCount == 1 && verified.FailedCount == 0,
            "no-code element assertions and URL waits pass on an expected browser state");
        workflow.Steps[0].Target = "#missing";
        var rejected = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(rejected.FailedCount == 1 && rejected.StepsNeedAttention.Single().StepType == "AssertVisible",
            "failed readiness assertion stops safely and reports its bounded step type");
    }

    private static void Check(bool condition, string description) => EngineBehaviorTests.Check(condition, description);
}
