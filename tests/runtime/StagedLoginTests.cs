using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;

internal static class StagedLoginTests
{
    public static async Task RunAsync(IPlaywright playwright)
    {
        await EmailThenPasswordAsync(playwright);
        await UnapprovedIdpAsync(playwright);
        await MfaRequiresHumanAsync(playwright);
        await AmbiguousButtonDoesNotGuessAsync(playwright);

        Check(AutomationEngine.IsUsernameStageSubmitLabel("Log In") &&
              AutomationEngine.IsUsernameStageSubmitLabel("Next") &&
              AutomationEngine.IsUsernameStageSubmitLabel("Continue") &&
              !AutomationEngine.IsUsernameStageSubmitLabel("Log in with SSO") &&
              !AutomationEngine.IsUsernameStageSubmitLabel("Sign in with Microsoft") &&
              !AutomationEngine.IsUsernameStageSubmitLabel("Forgot password") &&
              !AutomationEngine.IsUsernameStageSubmitLabel("Verify code"),
              "built-in username progression recognizes local-login buttons and refuses SSO/MFA controls");
        Console.WriteLine("PASS multi-stage login regression suite");
    }

    private static async Task EmailThenPasswordAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        await context.RouteAsync("https://staged.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = route.Request.Url.EndsWith("/login", StringComparison.Ordinal)
                ? "<form onsubmit=\"event.preventDefault();location.href='/password'\"><input id='email' type='email'><button type='submit'>Log In</button></form>"
                : route.Request.Url.EndsWith("/password", StringComparison.Ordinal)
                ? "<form onsubmit=\"event.preventDefault();location.href='/dashboard'\"><input id='secret' type='password'><button type='submit'>Log In</button></form>"
                : "<h1 id='signed-in'>Signed-in dashboard</h1>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://staged.test/login");
        var vault = new CredentialVault();
        var workflow = NewWorkflow(page.Url);
        await using var engine = new AutomationEngine(vault);
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.FinishedCount == 1 && result.LoginNeedsAttention.Count == 0 &&
              page.Url.EndsWith("/dashboard") && vault.ReadCount == 1,
              "two-page email -> Log In -> password -> Log In works with NO recorded actions or Continue selector");
        var repeated = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(repeated.FinishedCount == 1 && vault.ReadCount == 1 &&
              page.Url.EndsWith("/dashboard"),
              "repeated Run all reuses signed-in dashboard without resubmitting credentials");
    }

    private static async Task UnapprovedIdpAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        await context.RouteAsync("https://first.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = "<form onsubmit=\"event.preventDefault();location.href='https://identity.test/password'\"><input id='email' type='email'><button type='submit'>Continue</button></form>"
        }));
        await context.RouteAsync("https://identity.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = route.Request.Url.EndsWith("/password", StringComparison.Ordinal)
                ? "<form onsubmit=\"event.preventDefault();location.href='/dashboard'\"><input id='secret' type='password'><button type='submit'>Sign in</button></form>"
                : "<main id='signed-in'>Authenticated</main>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://first.test/login");
        var workflow = NewWorkflow(page.Url);
        var vault = new CredentialVault();
        await using var engine = new AutomationEngine(vault);
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var approvals = new List<string>();
        engine.ApproveLoginOriginAsync = (_, origin) => {
            approvals.Add(origin);
            return Task.FromResult(false);
        };
        var denied = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(denied.LoginNeedsAttention.Count == 1 &&
              approvals.SequenceEqual(new[] { "https://identity.test" }) &&
              page.Url.Contains("identity.test/password") &&
              await page.InputValueAsync("#secret") == "",
              "SSO/IdP password is NEVER sent to an unapproved second website");

        engine.ApproveLoginOriginAsync = (_, origin) =>
            Task.FromResult(origin == "https://identity.test");
        var approved = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(approved.FinishedCount == 1 && approved.LoginNeedsAttention.Count == 0 &&
              page.Url == "https://identity.test/dashboard",
              "explicit approval permits password insertion at the redirected IdP origin");
    }

    private static async Task MfaRequiresHumanAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        await context.RouteAsync("https://mfa-staged.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = route.Request.Url.EndsWith("/login", StringComparison.Ordinal)
                ? "<form onsubmit=\"event.preventDefault();location.href='/password'\"><input id='email' type='email'><button type='submit'>Next</button></form>"
                : route.Request.Url.EndsWith("/password", StringComparison.Ordinal)
                ? "<form onsubmit=\"event.preventDefault();location.href='/mfa'\"><input id='secret' type='password'><button type='submit'>Sign in</button></form>"
                : "<h1>Verification required</h1><input autocomplete='one-time-code'><button>Verify code</button><button id='followup'>Do not click</button>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://mfa-staged.test/login");
        var workflow = NewWorkflow(page.Url);
        workflow.Steps.Add(new WorkflowStep { Type = "Click", Target = "#followup" });
        await using var engine = new AutomationEngine(new CredentialVault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.LoginNeedsAttention.Count == 1 && result.FinishedCount == 0 &&
              page.Url.EndsWith("/mfa") && await page.Locator("input[autocomplete='one-time-code']").CountAsync() == 1,
              "MFA page blocks success and follow-up actions; automation stops for human verification");
    }

    private static async Task AmbiguousButtonDoesNotGuessAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        await context.RouteAsync("https://ambiguous.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = "<input id='email' type='email'><button id='one' onclick=\"document.body.dataset.clicked='one'\">Log In</button><button id='two' onclick=\"document.body.dataset.clicked='two'\">Next</button>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://ambiguous.test/login");
        var workflow = NewWorkflow(page.Url);
        workflow.LoginWaitSeconds = 5;
        await using var engine = new AutomationEngine(new CredentialVault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.LoginNeedsAttention.Count == 1 &&
              await page.GetAttributeAsync("body", "data-clicked") is null,
              "ambiguous local-login buttons are not clicked blindly");
    }

    private static WebsiteWorkflow NewWorkflow(string url) => new()
    {
        StartUrl = url,
        Name = "Synthetic multi-stage login",
        UsernameSelector = "#email",
        UsernameContinueSelector = "",
        PasswordSelector = "#secret",
        SubmitSelector = "button[type='submit']",
        LoginSuccessSelector = "#signed-in",
        LoginWaitSeconds = 5,
        Steps = new()
    };

    private static void Check(bool condition, string reason) =>
        EngineBehaviorTests.Check(condition, reason);
}
