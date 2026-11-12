using System.Reflection;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;

internal static class EngineBehaviorTests
{
    public static async Task RunAsync(IPlaywright playwright, string[] args)
    {
        if (args.Contains("--document-bound-only") || args.Contains("--profile-only")) return;
        await PrivacyAsync(playwright);
        await ExactOriginAsync(playwright);
        await SsoAsync(playwright);
        await ExplicitSignInConfirmationAsync(playwright);
        await NpmDashboardAfterStaleSelectorAsync(playwright);
        await MfaRedirectNeverVerifiesAsync(playwright);
        await CredentialContinuationCancellationAsync(playwright);
        await ApprovalPurposeAndStaleDestinationAsync(playwright);
        await BrowserSelectionSessionSwitchAsync(playwright);
    }

    private static async Task ApprovalPurposeAndStaleDestinationAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        await context.RouteAsync("https://typed-consent.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html", Body = "<input id='user'><input id='password' type='password'>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://typed-consent.test/login");
        var workflow = new WebsiteWorkflow
        {
            StartUrl = "https://different.test", AutoLogin = true,
            UsernameSelector = "#user", PasswordSelector = "#password"
        };
        await using var engine = new AutomationEngine(new CredentialVault());
        Track(engine, workflow, context, page);
        var seen = new List<bool>();
        engine.ApproveCredentialOriginAsync = (_, origin, password, ct) =>
        {
            seen.Add(password);
            // Accept a username-only grant, but never grant the password.
            if (!password) workflow.ApprovedLoginOrigins.Add(origin);
            return Task.FromResult(!password);
        };
        var fill = typeof(AutomationEngine).GetMethod("FillCredentialAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await using var username = await page.QuerySelectorAsync("#user")
            ?? throw new Exception("Username test input missing");
        await using var passwordField = await page.QuerySelectorAsync("#password")
            ?? throw new Exception("Password test input missing");
        await (Task<string?>)fill.Invoke(engine, new object?[] {
            workflow, page, "#user", "synthetic-username", CancellationToken.None,
            null, username, null, false, null })!;
        var refused = false;
        try
        {
            await (Task<string?>)fill.Invoke(engine, new object?[] {
                workflow, page, "#password", "synthetic-password", CancellationToken.None,
                null, passwordField, null, true, null })!;
        }
        catch (InvalidOperationException) { refused = true; }
        Check(seen.Contains(false) && seen.Contains(true) && refused &&
            string.IsNullOrEmpty(workflow.PasswordCredentialOrigin) &&
            await page.InputValueAsync("#password") == "",
            "username-only origin grant cannot authorize password fill");

        // An old approval returns after an unrelated password destination edit.
        // The engine must not overwrite the new mapping after awaiting a modal.
        workflow.PasswordCredentialOrigin = "";
        engine.ApproveCredentialOriginAsync = async (_, _, password, _) =>
        {
            if (password) workflow.PasswordCredentialOrigin = "https://changed.test";
            await Task.Yield();
            return true;
        };
        refused = false;
        // FillCredentialAsync takes ownership of a selected handle. Query a fresh
        // handle for this separate attempt instead of reusing the disposed one.
        await using var retryPasswordField = await page.QuerySelectorAsync("#password")
            ?? throw new Exception("Retry password input missing");
        try
        {
            await (Task<string?>)fill.Invoke(engine, new object?[] {
                workflow, page, "#password", "synthetic-password", CancellationToken.None,
                null, retryPasswordField, null, true, null })!;
        }
        catch (InvalidOperationException) { refused = true; }
        Check(refused && workflow.PasswordCredentialOrigin == "https://changed.test" &&
            await page.InputValueAsync("#password") == "",
            "late approval cannot overwrite competing password destination");
    }

    private static async Task BrowserSelectionSessionSwitchAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var context = await browser.NewContextAsync();
        await context.RouteAsync("https://browser-choice.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html", Body = "<h1>Dashboard</h1>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://browser-choice.test/");
        var wf = new WebsiteWorkflow { StartUrl = page.Url, Browser = "Edge", AutoLogin = false };
        await using var engine = new AutomationEngine(new CredentialVault());
        Track(engine, wf, context, page, "Edge");
        wf.Browser = "Firefox";
        var method = typeof(AutomationEngine).GetMethod("GetOrCreatePageCoreAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            var pending = (Task<IPage>)method.Invoke(engine, new object[] { wf, CancellationToken.None })!;
            await pending;
        }
        catch (Exception) { /* Firefox is not installed in this synthetic environment. */ }
        Check(page.IsClosed && context.Pages.All(p => p.IsClosed),
            "changing browser closes the prior Edge session instead of reusing its window");
    }

    private static async Task ExplicitSignInConfirmationAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        await context.RouteAsync("https://confirmed.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = route.Request.Url.EndsWith("/login", StringComparison.Ordinal)
                ? "<input type='password'><button>Sign in</button>"
                : "<h1>Signed-in dashboard</h1><button id='next' onclick='document.body.dataset.followed=String.fromCharCode(121,101,115)'>Continue</button>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://confirmed.test/dashboard");
        var wf = new WebsiteWorkflow
        {
            StartUrl = "https://confirmed.test/dashboard", AutoLogin = true,
            UsernameSelector = "#user", PasswordSelector = "#password",
            LoginSuccessSelector = "#old-missing-success"
        };
        wf.Steps.Add(new WorkflowStep { Type = "Click", Target = "#next" });
        await using var engine = new AutomationEngine(new CredentialVault());
        Track(engine, wf, context, page);
        var before = await engine.RunAsync(new[] { wf }, CancellationToken.None);
        Check(before.LoginNeedsAttention.Count == 1 &&
            await page.GetAttributeAsync("body", "data-followed") is null,
            "unverified login cannot run follow-up actions before human confirmation");
        Check(await engine.ConfirmSignedInAsync(wf), "the user can confirm a visible authenticated dashboard");
        var after = await engine.RunAsync(new[] { wf }, CancellationToken.None);
        Check(after.FinishedCount == 1 && after.LoginNeedsAttention.Count == 0 &&
            await page.GetAttributeAsync("body", "data-followed") == "yes",
            "explicit current-session sign-in confirmation permits follow-up actions");
        await page.GotoAsync("https://confirmed.test/login");
        Check(!await engine.ConfirmSignedInAsync(wf),
            "a fresh visible password challenge cannot be manually confirmed");
    }

    private static async Task NpmDashboardAfterStaleSelectorAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        await context.RouteAsync("https://npm-fixture.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = route.Request.Url.EndsWith("/login", StringComparison.OrdinalIgnoreCase)
                ? "<form onsubmit='event.preventDefault();location.href=String.fromCharCode(47);'><input id='user'><input id='password' type='password'><button type='submit'>Sign in</button></form>"
                : "<nav><a href='/'>Dashboard</a><a href='/hosts'>Proxy Hosts</a></nav><main id='signed-in'>Proxy Hosts</main>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://npm-fixture.test/login");
        var workflow = new WebsiteWorkflow
        {
            StartUrl = page.Url, UsernameSelector = "#user", PasswordSelector = "#password",
            SubmitSelector = "button[type=submit]", LoginSuccessSelector = "#old-dashboard-selector",
            LoginWaitSeconds = 5
        };
        await using var engine = new AutomationEngine(new CredentialVault());
        Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.FinishedCount == 1 && result.LoginNeedsAttention.Count == 0 &&
            page.Url == "https://npm-fixture.test/" &&
            await page.Locator("#signed-in").CountAsync() == 1,
            "NPM-style /login -> dashboard succeeds even when saved completion selector is stale");
        var repeat = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(repeat.FinishedCount == 1 && repeat.LoginNeedsAttention.Count == 0 &&
            await page.Locator("#signed-in").CountAsync() == 1,
            "rerunning a saved login website reuses the dashboard without demanding another password form");
    }

    private static async Task MfaRedirectNeverVerifiesAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        await context.RouteAsync("https://mfa-fixture.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = route.Request.Url.EndsWith("/login", StringComparison.OrdinalIgnoreCase)
                ? "<form onsubmit='event.preventDefault();location.href=String.fromCharCode(47,97,117,116,104);'><input id='user'><input id='password' type='password'><button type='submit'>Sign in</button></form>"
                : "<h1>Verification required</h1><input autocomplete='one-time-code'>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://mfa-fixture.test/login");
        var workflow = new WebsiteWorkflow
        {
            StartUrl = page.Url, UsernameSelector = "#user", PasswordSelector = "#password",
            SubmitSelector = "button[type=submit]", LoginSuccessSelector = "#signed-in",
            LoginWaitSeconds = 5
        };
        await using var engine = new AutomationEngine(new CredentialVault());
        Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.FinishedCount == 0 && result.LoginNeedsAttention.Count == 1 &&
            page.Url.EndsWith("/auth", StringComparison.OrdinalIgnoreCase),
            "MFA redirect must not be mistaken for a completed dashboard sign-in");
    }

    private static async Task CredentialContinuationCancellationAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(2500);
        context.SetDefaultNavigationTimeout(15_000);
        await context.RouteAsync("**/*", r => r.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = "<input id='user' oninput='cancelFixture()'><button id='next' onclick=\"document.body.dataset.continued='yes'\">next</button><p id='ready'>Ready</p>"
        }));
        using var cancellation = new CancellationTokenSource();
        var page = await context.NewPageAsync();
        await page.ExposeFunctionAsync("cancelFixture", () => cancellation.Cancel());
        await page.GotoAsync("https://credential-cancel.test/");
        var workflow = new WebsiteWorkflow
        {
            StartUrl = page.Url, UsernameSelector = "#user", PasswordSelector = "", SubmitSelector = "",
            UsernameContinueSelector = "#next", LoginSuccessSelector = "#ready"
        };
        await using var engine = new AutomationEngine(new CredentialVault());
        Track(engine, workflow, context, page);
        var cancelled = await EngineLifecycleTests.CancelledAsync(engine.RunAsync(new[] { workflow }, cancellation.Token));
        Check(cancelled && cancellation.IsCancellationRequested && await page.GetAttributeAsync("body", "data-continued") is null,
            "cancellation delivered by credential input prevents dispatching the username continuation");
    }

    private static async Task SsoAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(2500);
        context.SetDefaultNavigationTimeout(15_000);
        await context.RouteAsync("**/*", r => r.FulfillAsync(new() { ContentType = "text/html", Body = "<h1>MFA verification required</h1><input id='user'><input id='pass' type='password'><button id='followup' onclick=\"document.body.dataset.followed='yes'\">follow up</button>" }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://sso.test/");
        var workflow = new WebsiteWorkflow { StartUrl = page.Url, LoginMode = "SSO", UsernameSelector = "#user", PasswordSelector = "#pass", DestinationUrl = "https://sso.test/destination" };
        workflow.Steps.Add(new() { Type = "Click", Target = "#followup" });
        var vault = new CredentialVault();
        await using var engine = new AutomationEngine(vault);
        Track(engine, workflow, context, page);
        var states = new List<AutomationEngine.WorkflowRunState>();
        engine.OnWorkflowStatus += (_, state) => states.Add(state);
        var visitedDestination = false;
        page.Request += (_, r) => visitedDestination |= r.Url.EndsWith("/destination", StringComparison.Ordinal);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(vault.ReadCount == 0 && await page.InputValueAsync("#user") == "" && await page.InputValueAsync("#pass") == "",
            "SSO never reads or inserts stored username or password");
        Check(result.LoginNeedsAttention.Count == 1 && result.FinishedCount == 0 && result.FailedCount == 0 &&
            !visitedDestination && await page.GetAttributeAsync("body", "data-followed") is null &&
            states.SequenceEqual(new[] { AutomationEngine.WorkflowRunState.Running, AutomationEngine.WorkflowRunState.WaitingForSignIn }),
            "SSO manual challenge pauses follow-up and destination with WaitingForSignIn status");
        await page.SetContentAsync("<h1>Ready</h1><button id='followup' onclick=\"document.body.dataset.followed='yes'\">follow up</button>");
        workflow.DestinationUrl = "";
        workflow.LoginSuccessSelector = "#followup";
        var completed = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(vault.ReadCount == 0 && completed.FinishedCount == 1 && await page.GetAttributeAsync("body", "data-followed") == "yes",
            "SSO without a detected challenge continues without touching the vault");
    }

    private static async Task ExactOriginAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var failures = new List<string>();
        foreach (var current in new[] { "http://origin.test/old", "https://origin.test:8443/old", "https://origin.test/already-signed-in" })
        {
            await using var context = await browser.NewContextAsync();
            await context.RouteAsync("**/*", r => r.FulfillAsync(new() { ContentType = "text/html", Body = "<h1>Ready</h1>" }));
            var page = await context.NewPageAsync();
            await page.GotoAsync(current);
            var workflow = new WebsiteWorkflow { StartUrl = "https://origin.test/start", AutoLogin = false };
            await using var engine = new AutomationEngine(new CredentialVault());
            Track(engine, workflow, context, page);
            var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
            var expected = current.EndsWith("already-signed-in", StringComparison.Ordinal) ? current : workflow.StartUrl;
            CheckOrCollect(result.FinishedCount == 1 && page.Url == expected,
                "page reuse requires exact scheme and authority: " + current, failures);
        }
        if (failures.Count > 0) throw new Exception(string.Join("; ", failures));
    }

    private static async Task PrivacyAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(2500);
        context.SetDefaultNavigationTimeout(15_000);
        await context.RouteAsync("**/*", r => r.FulfillAsync(new() { ContentType = "text/html", Body = "<input id='user'><input id='pass' type='password'>" }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://privacy.test/");
        await using var engine = new AutomationEngine(new CredentialVault());
        var privateError = "SENTINEL-private-secret-in-invalid-operation";
        Check(AutomationEngine.PublicWorkflowFailureReason(
            new InvalidOperationException(privateError)).IndexOf(privateError,
                StringComparison.Ordinal) < 0,
            "untrusted InvalidOperationException text is not shown in run summaries");
        Check(AutomationEngine.PublicWorkflowFailureReason(
            new InvalidOperationException("Credential document changed. Retry sign-in manually."))
            == RunIssueNotice.Message(RunIssueCode.DocumentChanged),
            "known safe failure remains actionable");
        var logs = new List<string>();
        engine.OnLog += logs.Add;
        var workflow = new WebsiteWorkflow { StartUrl = "private-invalid-url", AutoLogin = false };
        var failed = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        var failures = new List<string>();
        CheckOrCollect(failed.FailedCount == 1 && !logs.Any(l => l.Contains("private-invalid-url", StringComparison.Ordinal)),
            "generic workflow errors never echo a private URL", failures);
        logs.Clear();
        workflow.StartUrl = page.Url;
        workflow.AutoLogin = true;
        workflow.UsernameSelector = "#user";
        workflow.UsernameContinueSelector = "[private-invalid-selector";
        Track(engine, workflow, context, page);
        var login = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        CheckOrCollect(login.LoginNeedsAttention.Count == 1 && !logs.Any(l => l.Contains("private-invalid-selector", StringComparison.Ordinal)),
            "auto-login errors never echo raw Playwright selector diagnostics", failures);
        logs.Clear();
        await page.SetContentAsync("<input id='private-selector' readonly>");
        workflow.AutoLogin = false;
        workflow.Steps.Add(new() { Type = "Fill", Target = "#private-selector", Value = "private-secret-value" });
        var fill = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        CheckOrCollect(fill.StepsNeedAttention.Single().StepType == "Fill" &&
            !logs.Any(l => l.Contains("private-", StringComparison.Ordinal)),
            "failed Fill exposes only fixed action type, never its private selector or value", failures);
        if (failures.Count > 0) throw new Exception(string.Join("; ", failures));
    }

    internal static void Track(AutomationEngine engine, WebsiteWorkflow workflow, IBrowserContext context, IPage page, string? browser = null)
    {
        // Inert ownership seam only: route real browser pages through production execution without native profile launch.
        var sessions = (System.Collections.IDictionary)typeof(AutomationEngine)
            .GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
        var type = typeof(AutomationEngine).GetNestedType("TrackedSession", BindingFlags.NonPublic)!;
        var tracked = Activator.CreateInstance(type, context, page)!;
        if (browser is not null)
            type.GetProperty("Browser")!.SetValue(tracked, browser);
        sessions.Add(workflow.Id, tracked);
    }

    internal static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL " + description);
        Console.WriteLine("PASS " + description);
    }

    private static void CheckOrCollect(bool condition, string description, List<string> failures)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + description);
        if (!condition) failures.Add(description);
    }
}
