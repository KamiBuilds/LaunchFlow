using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;

internal static class ManualConfirmationRaceTests
{
    public static async Task RunAsync(IPlaywright playwright)
    {
        var failures = new List<string>();
        void Check(bool condition, string name)
        {
            Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
            if (!condition) failures.Add(name);
        }

        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        foreach (var scenario in new[] { "initial-reload", "reuse-reload", "cancel", "automation", "credential-fill", "credential-storage", "host" })
        {
            await using var context = await browser.NewContextAsync();
            context.SetDefaultTimeout(5_000);
            var challenge = false;
            await context.RouteAsync("https://confirmation.test/**", route => route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = (challenge ? "<input type='password'>" : "<h1>Dashboard</h1>") +
                    "<button id='next' onclick=\"document.body.dataset.followed='yes'\">Continue</button>"
            }));
            var real = await context.NewPageAsync();
            await real.GotoAsync("https://confirmation.test/dashboard", new() { Timeout = 30_000 });
            var values = new Dictionary<string, object?>();
            using var provider = EnterprisePolicy.UseProviderForTesting(() => values);
            using var cancellation = new CancellationTokenSource();
            var armed = false;
            var boundaryReached = false;

            async Task<int> InspectBody(ILocator locator)
            {
                // Forward the real inspection, then exercise the await-return gap.
                // This substitutes no application decision or browser result.
                var count = await locator.CountAsync();
                if (armed)
                {
                    armed = false;
                    boundaryReached = true;
                    if (scenario is "initial-reload" or "reuse-reload")
                    {
                        challenge = true;
                        await real.ReloadAsync(new() { Timeout = 30_000 });
                    }
                    else if (scenario == "cancel") cancellation.Cancel();
                    else values[scenario switch
                    {
                        "automation" => "DisableAutomation",
                        "credential-fill" => "DisableCredentialFill",
                        "credential-storage" => "DisableCredentialStorage",
                        _ => "AllowedHosts"
                    }] = scenario == "host" ? "different.test" : 1;
                }
                return count;
            }

            var page = ConfirmationForwarder<IPage>.Wrap(real, (method, arguments) =>
            {
                if (method.Name != nameof(IPage.Locator) || (string)arguments![0]! != "body")
                    return (false, null);
                var body = real.Locator("body");
                return (true, ConfirmationForwarder<ILocator>.Wrap(body, (member, _) =>
                    member.Name == nameof(ILocator.CountAsync) ? (true, InspectBody(body)) : (false, null)));
            });
            var workflow = new WebsiteWorkflow
            {
                StartUrl = real.Url,
                AutoLogin = true,
                LoginMode = "SSO",
                LoginSuccessSelector = "#missing-success-marker",
                LoginWaitSeconds = 1
            };
            workflow.Steps.Add(new() { Type = "Click", Target = "#next" });
            await using var engine = new AutomationEngine(new CredentialVault());
            Track(engine, workflow, context, page);

            if (scenario != "initial-reload")
                Check(await engine.ConfirmSignedInAsync(workflow), scenario + ": healthy explicit confirmation is accepted");
            armed = true;
            if (scenario == "reuse-reload")
            {
                var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
                Check(boundaryReached && result.FinishedCount == 0 &&
                    await real.GetAttributeAsync("body", "data-followed") is null &&
                    await real.Locator("input[type=password]:visible").CountAsync() == 1,
                    "same-URL reload during acknowledgment reuse blocks follow-up action on new challenge document");
            }
            else
            {
                var accepted = false;
                try { accepted = await engine.ConfirmSignedInAsync(workflow, cancellation.Token); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (InvalidOperationException) when (scenario is "automation" or "credential-fill" or "credential-storage" or "host") { }
                Check(boundaryReached && !accepted, scenario + ": confirmation cannot authorize state revoked during inspection");
            }
        }

        await using (var context = await browser.NewContextAsync())
        {
            await context.RouteAsync("https://confirmation.test/**", route => route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = "<button id='next' onclick=\"document.body.dataset.followed='yes'\">Continue</button>"
            }));
            var page = await context.NewPageAsync();
            await page.GotoAsync("https://confirmation.test/dashboard", new() { Timeout = 30_000 });
            var workflow = new WebsiteWorkflow { StartUrl = page.Url, AutoLogin = true, LoginMode = "SSO", LoginSuccessSelector = "#missing" };
            workflow.Steps.Add(new() { Type = "Click", Target = "#next" });
            await using var engine = new AutomationEngine(new CredentialVault());
            Track(engine, workflow, context, page);
            Check(await engine.ConfirmSignedInAsync(workflow), "unchanged-page positive control accepts explicit sign-in");
            var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
            Check(result.FinishedCount == 1 && await page.GetAttributeAsync("body", "data-followed") == "yes",
                "unchanged authorized acknowledgment still permits actual follow-up dispatch");
        }
        if (failures.Count > 0)
            throw new InvalidOperationException("Manual confirmation regressions: " + string.Join("; ", failures));
    }

    private static void Track(AutomationEngine engine, WebsiteWorkflow workflow, IBrowserContext context, IPage page)
    {
        // Explicit session-ownership seam; all authorization and execution are production code.
        var sessions = (IDictionary)typeof(AutomationEngine).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
        var type = typeof(AutomationEngine).GetNestedType("TrackedSession", BindingFlags.NonPublic)!;
        sessions.Add(workflow.Id, Activator.CreateInstance(type, context, page));
    }
}

public class ConfirmationForwarder<T> : DispatchProxy where T : class
{
    private T _target = null!;
    private Func<MethodInfo, object?[]?, (bool Handled, object? Result)> _intercept = null!;
    public static T Wrap(T target, Func<MethodInfo, object?[]?, (bool, object?)> intercept)
    {
        var proxy = Create<T, ConfirmationForwarder<T>>();
        var implementation = (ConfirmationForwarder<T>)(object)proxy;
        implementation._target = target;
        implementation._intercept = intercept;
        return proxy;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        var (handled, result) = _intercept(method!, arguments);
        if (handled) return result;
        try { return method!.Invoke(_target, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }
}
