using System.Reflection;
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
        var workflow = new WebsiteWorkflow { StartUrl = page.Url, AutoLogin = false };
        workflow.Steps.Add(new() { Type = "Click", Target = "#missing" });
        workflow.Steps.Add(new() { Type = "Click", Target = "#followup" });
        await using var engine = new AutomationEngine(new CredentialVault());
        var sessions = (System.Collections.IDictionary)typeof(AutomationEngine)
            .GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
        var sessionType = typeof(AutomationEngine).GetNestedType("TrackedSession", BindingFlags.NonPublic)!;
        sessions.Add(workflow.Id, Activator.CreateInstance(sessionType, context, page));
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(await page.GetAttributeAsync("body", "data-followed") is null,
            "a failed step blocks later workflow actions");
        Check(result.FailedCount == 1 && result.FinishedCount == 0,
            "a failed step is not reported as a completed workflow");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL " + description);
        Console.WriteLine("PASS " + description);
    }
}
