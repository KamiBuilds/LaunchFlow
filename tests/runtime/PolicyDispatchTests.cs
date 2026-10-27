using System.Text.Json;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;

internal static class PolicyDispatchTests
{
    public static async Task RunAsync(IPlaywright playwright, string[] args)
    {
        var group = args.FirstOrDefault(a => a.StartsWith("--policy-dispatch-group=", StringComparison.Ordinal))?.Split('=', 2)[1];
        if (group is not null and not "click" and not "navigation" and not "redirect")
            throw new ArgumentException("Unknown policy dispatch group.");
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var failures = new List<string>();
        if (group is null or "click") await CredentialContinuationAsync(browser, failures);
        if (group is null or "navigation") await InitialNavigationAsync(browser, failures);
        if (group is null or "redirect") await PostLoginRedirectAsync(browser, failures);
        if (failures.Count > 0) throw new Exception(string.Join("; ", failures));
    }

    private static async Task CredentialContinuationAsync(IBrowser browser, List<string> failures)
    {
        foreach (var type in new[] { "continue", "submit" })
        foreach (var revocation in new[] { "automation", "host", "navigation" })
        {
            await using var context = await browser.NewContextAsync();
            context.SetDefaultTimeout(2500);
            await context.RouteAsync("**/*", r => r.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = "<input id='user'><button id='action' onclick=\"document.body.dataset.acted='yes'\">Action</button>"
            }));
            var actual = await context.NewPageAsync();
            await actual.GotoAsync("https://allowed.test/");
            var values = new Dictionary<string, object?> { ["AllowedHosts"] = "allowed.test" };
            using var policy = EnterprisePolicy.UseProviderForTesting(() => values);
            var revoked = false;
            var filled = false;
            async Task FillAsync(IElementHandle target, string value)
            {
                // Complete and observe a real credential fill before delivering the policy/document change.
                await target.FillAsync(value);
                filled = await target.InputValueAsync() == value;
                if (revocation == "automation") values["DisableAutomation"] = 1;
                if (revocation == "host") values["AllowedHosts"] = "other.test";
                if (revocation == "navigation") await actual.GotoAsync("https://blocked.test/");
                revoked = true;
            }
            async Task<IElementHandle?> QueryAsync(string selector)
            {
                var target = await actual.QuerySelectorAsync(selector);
                return target is null ? null : ForwardProxy<IElementHandle>.Create(target, (method, parameters) =>
                    method.Name == nameof(IElementHandle.FillAsync)
                        ? (true, FillAsync(target, (string)parameters![0]!)) : (false, null));
            }
            var page = ForwardProxy<IPage>.Create(actual, (method, parameters) =>
                method.Name == nameof(IPage.QuerySelectorAsync)
                    ? (true, QueryAsync((string)parameters![0]!)) : (false, null));
            var workflow = new WebsiteWorkflow
            {
                StartUrl = actual.Url, AutoLogin = true, UsernameSelector = "#user", PasswordSelector = "",
                LoginSuccessSelector = "body", UsernameContinueSelector = type == "continue" ? "#action" : "",
                SubmitSelector = type == "submit" ? "#action" : ""
            };
            await using var engine = new AutomationEngine(new CredentialVault());
            EngineBehaviorTests.Track(engine, workflow, context, page);
            var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
            var clicked = await actual.GetAttributeAsync("body", "data-acted") == "yes";
            Check(filled && revoked && !clicked && result.FinishedCount == 0 &&
                result.FailedCount + result.LoginNeedsAttention.Count == 1,
                $"auto-login-{type}-after-{revocation}: completed credential fill cannot authorize a later click (filled={filled}, revoked={revoked}, clicked={clicked}, failed={result.FailedCount}, finished={result.FinishedCount})", failures);
        }
    }

    private static async Task InitialNavigationAsync(IBrowser browser, List<string> failures)
    {
        foreach (var revocation in new[] { "automation", "host" })
        {
            await using var context = await browser.NewContextAsync();
            await context.RouteAsync("**/*", r => r.FulfillAsync(new() { ContentType = "text/html", Body = "<h1>Ready</h1>" }));
            var actual = await context.NewPageAsync();
            await actual.GotoAsync("https://existing.test/");
            var cdpActual = await context.NewCDPSessionAsync(actual);
            var values = new Dictionary<string, object?>();
            using var policy = EnterprisePolicy.UseProviderForTesting(() => values);
            var revoked = false;
            async Task<JsonElement?> RevokeAsync()
            {
                await Task.Yield();
                if (revocation == "automation") values["DisableAutomation"] = 1;
                else values["AllowedHosts"] = "existing.test";
                revoked = true;
                return null;
            }
            var cdp = ForwardProxy<ICDPSession>.Create(cdpActual, (method, parameters) =>
            {
                if (method.Name != nameof(ICDPSession.SendAsync)) return (false, null);
                return (string)parameters![0]! == "Browser.getWindowForTarget"
                    ? (true, Task.FromResult<JsonElement?>(JsonSerializer.Deserialize<JsonElement>("{\"windowId\":1}")))
                    : (true, RevokeAsync());
            });
            var ctx = ForwardProxy<IBrowserContext>.Create(context, (method, _) =>
                method.Name == nameof(IBrowserContext.NewCDPSessionAsync) ? (true, Task.FromResult(cdp)) : (false, null));
            var page = ForwardProxy<IPage>.Create(actual, (method, _) => method.Name == "get_Context" ? (true, ctx) : (false, null));
            var workflow = new WebsiteWorkflow { StartUrl = "https://allowed.test/start", AutoLogin = false };
            await using var engine = new AutomationEngine(new CredentialVault());
            EngineBehaviorTests.Track(engine, workflow, context, page);
            var dispatched = false;
            actual.Request += (_, request) => dispatched |= revoked && request.Url == workflow.StartUrl;
            var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
            Check(revoked && !dispatched && actual.Url == "https://existing.test/" && result.FailedCount == 1 && result.FinishedCount == 0,
                $"initial-navigation-after-positioning-{revocation}: no revoked navigation dispatch (revoked={revoked}, dispatched={dispatched}, url={actual.Url})", failures);
        }
    }

    private static async Task PostLoginRedirectAsync(IBrowser browser, List<string> failures)
    {
        await using var context = await browser.NewContextAsync();
        await context.RouteAsync("**/*", r => r.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = r.Request.Url == "https://allowed.test/"
                ? "<input id='user'><button id='action' onclick=\"location.href='https://blocked.test/after-login'\">Sign in</button>"
                : "<h1>Ready</h1>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://allowed.test/");
        var workflow = new WebsiteWorkflow
        {
            StartUrl = page.Url, AutoLogin = true, UsernameSelector = "#user", PasswordSelector = "",
            SubmitSelector = "#action", LoginSuccessSelector = "body", DestinationUrl = "https://allowed.test/final"
        };
        var reachedBlocked = false;
        var reachedFinal = false;
        page.Request += (_, request) =>
        {
            reachedBlocked |= request.Url == "https://blocked.test/after-login";
            reachedFinal |= request.Url == workflow.DestinationUrl;
        };
        using var policy = EnterprisePolicy.UseProviderForTesting(() => new Dictionary<string, object?> { ["AllowedHosts"] = "allowed.test" });
        await using var engine = new AutomationEngine(new CredentialVault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(reachedBlocked && !reachedFinal && page.Url == "https://blocked.test/after-login" && result.FinishedCount == 0 &&
            result.FailedCount + result.LoginNeedsAttention.Count == 1,
            $"post-login-blocked-redirect-followed-by-destination: allowed destination cannot mask denied redirect (blocked={reachedBlocked}, final={reachedFinal}, failed={result.FailedCount}, finished={result.FinishedCount})", failures);
    }

    private static void Check(bool condition, string description, List<string> failures)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + description);
        if (!condition) failures.Add(description);
    }
}
