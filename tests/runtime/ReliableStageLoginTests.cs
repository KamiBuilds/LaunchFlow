using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;

internal static class ReliableStageLoginTests
{
    private static readonly string TestUsername = "test@example.invalid";
    private static readonly string TestPassword = "synthetic-password-never-send-outside-test";

    public static async Task RunAsync(IPlaywright playwright)
    {
        await TwoDocumentAsync(playwright, "log-in", "Log In", "Log In", false, false);
        await TwoDocumentAsync(playwright, "sign-in", "Sign in", "Sign in", false, false);
        await TwoDocumentAsync(playwright, "next", "Next", "Next", false, false);
        await TwoDocumentAsync(playwright, "continue", "Continue", "Continue", false, false);
        await TwoDocumentAsync(playwright, "thousandeyes-shape", "Log In", "Next", true, false);
        await TwoDocumentAsync(playwright, "delayed-action", "Log In", "Next", true, true);
        await PasswordOnlyNextAsync(playwright);
        await SameUrlAppAsync(playwright);
        await FalseSuccessAsync(playwright);
        await ReusedAuthenticatedSessionAsync(playwright);
        await SsoWithoutEvidenceAsync(playwright);
        await LockedAccountDoesNotCompleteAsync(playwright);
        Console.WriteLine("PASS stage-aware real Chromium login suite");
    }

    private static WebsiteWorkflow Workflow(string url) => new()
    {
        Name = "Synthetic stage-aware", StartUrl = url, LoginWaitSeconds = 5,
        LoginSuccessSelector = "", LoginSuccessUrlContains = ""
    };

    private static CredentialVault Vault() => new()
    {
        Credential = new SiteCredential(TestUsername, TestPassword)
    };

    private static async Task TwoDocumentAsync(
        IPlaywright playwright, string scenario, string firstLabel, string lastLabel,
        bool thousandEyesForm, bool delayed)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(4000);
        var firstCount = 0;
        var lastCount = 0;
        var origin = "https://" + scenario + ".stage-test";
        await context.RouteAsync(origin + "/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (path == "/identifier-action")
            {
                Interlocked.Increment(ref firstCount);
                await route.FulfillAsync(new() { Status = 204 });
                return;
            }
            if (path == "/password-action")
            {
                Interlocked.Increment(ref lastCount);
                await route.FulfillAsync(new() { Status = 204 });
                return;
            }
            var firstForm = thousandEyesForm
                ? "<form id='ssoForm' action='/v2/login'>"
                : "<form id='identifier' action='/login'>";
            var firstInput = thousandEyesForm
                ? "<input type='text' id='email'>"
                : "<input type='email' id='email'>";
            var first = firstForm + firstInput +
                "<button id='submit-button' type='button' " + (delayed ? "disabled " : "") +
                "onclick=\"fetch('/identifier-action',{method:'POST'});location.href='/password'\">" +
                firstLabel + "</button></form>" +
                (delayed ? "<script>setTimeout(()=>document.getElementById('submit-button').disabled=false,450)</script>" : "");
            var button = "<button type='button' id='password-next' " +
                (delayed ? "disabled " : "") +
                "onclick=\"fetch('/password-action',{method:'POST'});location.href='/application'\">" +
                lastLabel + "</button>";
            var delay = delayed
                ? "<script>setTimeout(()=>document.getElementById('password-next').disabled=false,450)</script>"
                : "";
            var password = "<form id='login-password' action='/v2/login'>" +
                "<input id='secret' type='password' autocomplete='current-password'>" +
                button + "</form>" + delay;
            var app = "<nav><button aria-label='Account' type='button'>Account menu</button>" +
                "<a href='/logout'>Sign out</a></nav>" +
                "<button id='followup' onclick=\"document.body.dataset.followed='yes'\">Next workflow step</button>";
            await route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = path == "/login" ? first : path == "/password" ? password : app
            });
        });

        var page = await context.NewPageAsync();
        await page.GotoAsync(origin + "/login");
        var workflow = Workflow(origin + "/login");
        workflow.Steps.Add(new WorkflowStep { Type = "Click", Target = "#followup" });
        var vault = Vault();
        await using var engine = new AutomationEngine(vault);
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        var followed = await page.GetAttributeAsync("body", "data-followed") == "yes";
        var ok = result.FinishedCount == 1 && result.LoginNeedsAttention.Count == 0 &&
            page.Url == origin + "/application" &&
            firstCount == 1 && lastCount == 1 && vault.ReadCount == 1 && followed;
        Console.WriteLine($"STAGE_LF {scenario} first={firstCount} last={lastCount} path={new Uri(page.Url).AbsolutePath} finished={result.FinishedCount} attention={result.LoginNeedsAttention.Count} followed={followed}");
        EngineBehaviorTests.Check(ok,
            $"LF multi-step {scenario}: both owned buttons fired exactly once and app verification permitted follow-up");
    }

    private static async Task PasswordOnlyNextAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(4000);
        var count = 0;
        await context.RouteAsync("https://password-next.stage-test/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (path == "/finish")
            {
                Interlocked.Increment(ref count);
                await route.FulfillAsync(new() { Status = 204 });
                return;
            }
            var body = path == "/password" ?
                "<form id='local-login'><input id='secret' type='password' autocomplete='current-password'>" +
                "<button type='button' onclick=\"fetch('/finish',{method:'POST'});location.href='/application'\">Next</button></form>" :
                "<nav><button aria-label='Account'>Account</button><a href='/logout'>Sign out</a></nav>";
            await route.FulfillAsync(new() { ContentType = "text/html", Body = body });
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://password-next.stage-test/password");
        var workflow = Workflow("https://password-next.stage-test/login");
        await using var engine = new AutomationEngine(Vault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        EngineBehaviorTests.Check(count == 1 && result.FinishedCount == 1 && page.Url.EndsWith("/application"),
            "LF-08 resumed password stage accepts owned Next type=button");
    }

    private static async Task SameUrlAppAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(4000);
        await context.RouteAsync("https://spa-stage.stage-test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = """
                <div id="root">
                  <form id="identifier" action="/login">
                    <input id="email" type="email">
                    <button id="first" type="button" onclick="identifierStage()">Sign in</button>
                  </form>
                </div>
                <script>
                let firstCount=0, lastCount=0;
                function identifierStage() {
                  firstCount++;
                  document.getElementById('root').innerHTML =
                    '<form id="auth-password"><input id="secret" type="password" autocomplete="current-password">' +
                    '<button type="button" id="last" onclick="passwordStage()">Next</button></form>';
                }
                function passwordStage() {
                  lastCount++;
                  document.getElementById('root').innerHTML =
                    '<nav><button aria-label="Account">Account menu</button><a href="/logout">Sign out</a></nav>' +
                    '<button id="followup" onclick="document.body.dataset.followed=\'yes\'">Follow up</button>';
                }
                </script>
                """
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://spa-stage.stage-test/login");
        var workflow = Workflow(page.Url);
        workflow.Steps.Add(new WorkflowStep { Type = "Click", Target = "#followup" });
        await using var engine = new AutomationEngine(Vault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        var counts = await page.EvaluateAsync<int[]>("() => [firstCount,lastCount]");
        var followed = await page.GetAttributeAsync("body", "data-followed") == "yes";
        EngineBehaviorTests.Check(counts.SequenceEqual(new[] { 1, 1 }) && followed &&
            result.FinishedCount == 1 && result.LoginNeedsAttention.Count == 0 &&
            page.Url == "https://spa-stage.stage-test/login",
            "LF-10/18 same-URL SPA verifies authenticated application and continues once");
    }

    private static async Task FalseSuccessAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(4000);
        await context.RouteAsync("https://no-proof.stage-test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = route.Request.Url.EndsWith("/login") ?
                "<form id='login'><input id='email' type='email'><input id='secret' type='password'>" +
                "<button type='submit' onclick=\"event.preventDefault();location.href='/welcome'\">Sign in</button></form>"
                : "<h1>Welcome</h1><p>Please finish signing in.</p><a href='/account'>Account</a>" +
                  "<button id='followup' onclick=\"document.body.dataset.acted='yes'\">Do not click</button>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://no-proof.stage-test/login");
        var workflow = Workflow(page.Url);
        workflow.Steps.Add(new WorkflowStep { Type = "Click", Target = "#followup" });
        await using var engine = new AutomationEngine(Vault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        EngineBehaviorTests.Check(result.FinishedCount == 0 && result.LoginNeedsAttention.Count == 1 &&
            await page.GetAttributeAsync("body", "data-acted") is null,
            "LF-30 changed URL, welcome text and generic account link do not verify success");
    }
    private static async Task ReusedAuthenticatedSessionAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        await context.RouteAsync("https://already.stage-test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = "<nav><button aria-label='Account'>Settings</button><a href='/logout'>Sign out</a></nav>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://already.stage-test/login");
        var vault = Vault();
        var workflow = Workflow(page.Url);
        await using var engine = new AutomationEngine(vault);
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        EngineBehaviorTests.Check(result.FinishedCount == 1 && vault.ReadCount == 0,
            "LF-17 existing authenticated application does not re-read or re-submit stored credentials");
    }

    private static async Task SsoWithoutEvidenceAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        await context.RouteAsync("https://sso-unverified.stage-test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = "<h1>Welcome</h1><button id='followup' onclick=\"document.body.dataset.followed='yes'\">Continue</button>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://sso-unverified.stage-test/");
        var workflow = Workflow(page.Url);
        workflow.LoginMode = "SSO";
        workflow.Steps.Add(new WorkflowStep { Type = "Click", Target = "#followup" });
        var vault = Vault();
        await using var engine = new AutomationEngine(vault);
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        EngineBehaviorTests.Check(result.FinishedCount == 0 && result.LoginNeedsAttention.Count == 1 &&
            vault.ReadCount == 0 && await page.GetAttributeAsync("body", "data-followed") is null,
            "LF-31 browser-led SSO without target application proof never authorizes downstream actions");
    }

    private static async Task LockedAccountDoesNotCompleteAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(4000);
        var submissions = 0;
        await context.RouteAsync("https://locked.stage-test/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (path == "/submitted")
            {
                Interlocked.Increment(ref submissions);
                await route.FulfillAsync(new() { Status = 204 });
                return;
            }
            var html = path == "/login" ?
                "<form id='login'><input id='email' type='email'><input id='secret' type='password' " +
                "autocomplete='current-password'><button type='button' " +
                "onclick=\"fetch('/submitted',{method:'POST'});location.href='/locked'\">Sign in</button></form>"
                : "<h1>Account locked</h1><nav><button aria-label='Account'>Account</button>" +
                  "<a href='/logout'>Sign out</a></nav>" +
                  "<button id='followup' onclick=\"document.body.dataset.followed='yes'\">Followup</button>";
            await route.FulfillAsync(new() { ContentType = "text/html", Body = html });
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://locked.stage-test/login");
        var workflow = Workflow(page.Url);
        workflow.Steps.Add(new WorkflowStep { Type = "Click", Target = "#followup" });
        await using var engine = new AutomationEngine(Vault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        EngineBehaviorTests.Check(submissions == 1 && result.FinishedCount == 0 &&
            result.LoginNeedsAttention.Count == 1 &&
            await page.GetAttributeAsync("body", "data-followed") is null,
            "LF-27 account lockout never counts as authenticated, even with counterfeit account controls");
    }

}
