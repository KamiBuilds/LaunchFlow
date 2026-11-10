using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;

internal static class AutomaticEntryTests
{
    public static async Task RunAsync(IPlaywright playwright)
    {
        await HomeEntryAndGitHubFieldsAsync(playwright);
        await ModalSignInEntryAsync(playwright);
        await CombinedWithUnrelatedNewsletterAsync(playwright);
        await IdentifierThenPasswordAsync(playwright);
        await ResumedPasswordOnlyAsync(playwright);
        await DuplicateAndAmbiguousEntriesAsync(playwright);
        await ExternalFinalFormAssociationAsync(playwright);
        await UnrelatedFinalSubmitAsync(playwright);
        await SignupNeverReceivesPasswordAsync(playwright);
        await MarketingMfaTextDoesNotBlockAsync(playwright);
        await UnverifiedRedirectNeverRunsFollowupAsync(playwright);
        Console.WriteLine("PASS AUTO safe entry, semantic username and final form owner fixtures");
    }

    private static WebsiteWorkflow Workflow(string url) => new()
    {
        Name = "Synthetic automatic login", StartUrl = url, LoginWaitSeconds = 5,
        LoginSuccessSelector = "", LoginSuccessUrlContains = ""
    };

    private static CredentialVault Vault() => new()
    {
        Credential = new SiteCredential("test@example.invalid", "synthetic-password-only")
    };

    private static async Task HomeEntryAndGitHubFieldsAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        var loginRequests = 0;
        var newsletterRequests = 0;
        await context.RouteAsync("https://auto-home.test/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (path == "/newsletter-touched")
            {
                Interlocked.Increment(ref newsletterRequests);
                await route.FulfillAsync(new() { Status = 204 });
                return;
            }
            if (path == "/login") Interlocked.Increment(ref loginRequests);
            await route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = path switch
                {
                    "/" => """
                        <form id="newsletter"><input id="marketing" type="email"
                          oninput="fetch('/newsletter-touched',{method:'POST'})"></form>
                        <nav><a href="/login">Sign in</a></nav>
                        """,
                    "/login" => """
                        <form id="login" onsubmit="event.preventDefault();location.href='/dashboard'">
                          <input id="login_field" name="login" type="text" autocomplete="username">
                          <input id="password" type="password" autocomplete="current-password">
                          <input type="submit" value="Sign in">
                        </form>
                        """,
                    _ => "<main id='signed-in'>Account dashboard</main><button id='followup' onclick=\"document.body.dataset.followed='yes'\">Open</button>"
                }
            });
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://auto-home.test/");
        var workflow = Workflow(page.Url);
        workflow.Steps.Add(new WorkflowStep { Type = "Click", Target = "#followup" });
        var vault = Vault();
        await using var engine = new AutomationEngine(vault);
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.FinishedCount == 1 && result.LoginNeedsAttention.Count == 0 &&
              page.Url.EndsWith("/dashboard", StringComparison.Ordinal) &&
              await page.GetAttributeAsync("body", "data-followed") == "yes" &&
              loginRequests == 1 && newsletterRequests == 0 && vault.ReadCount == 1,
            "AUTO-01/06/07/24 homepage and native INPUT submit must work without touching newsletter or technical success selector");
        var again = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(again.FinishedCount == 1 && vault.ReadCount == 1 &&
              loginRequests == 1 && newsletterRequests == 0,
            "AUTO-23 repeated run must reuse existing signed-in page without fetching credentials");
    }

    private static async Task ModalSignInEntryAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        var unrelatedRequests = 0;
        await context.RouteAsync("https://auto-dialog.test/**", route =>
        {
            if (new Uri(route.Request.Url).AbsolutePath == "/unrelated")
            {
                Interlocked.Increment(ref unrelatedRequests);
                return route.FulfillAsync(new() { Status = 204 });
            }
            return route.FulfillAsync(new()
            {
            ContentType = "text/html",
            Body = new Uri(route.Request.Url).AbsolutePath == "/"
                ? """
                  <button aria-haspopup="dialog" aria-controls="sign-in" type="button"
                          onclick="document.getElementById('sign-in').hidden=false;window.dialogOpens++">
                      Sign in
                  </button>
                  <button type="button" onclick="fetch('/unrelated',{method:'POST'})">Sign in</button>
                  <div id="sign-in" role="dialog" hidden>
                    <form id="login" onsubmit="event.preventDefault();location.href='/dashboard'">
                      <input autocomplete="username" id="login_field">
                      <input type="password" autocomplete="current-password">
                      <button type="submit">Log in</button>
                    </form>
                  </div>
                  <script>window.dialogOpens=0;</script>
                  """
                : "<h1 id='signed-in'>Dashboard</h1>"
            });
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://auto-dialog.test/");
        var workflow = Workflow(page.Url);
        var vault = Vault();
        await using var engine = new AutomationEngine(vault);
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.FinishedCount == 1 && result.LoginNeedsAttention.Count == 0 &&
              page.Url.EndsWith("/dashboard") && vault.ReadCount == 1 && unrelatedRequests == 0,
            "AUTO-01 homepage declared sign-in dialog opens, while unrelated button is ignored");
    }

    private static async Task CombinedWithUnrelatedNewsletterAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        var unexpected = 0;
        await context.RouteAsync("https://auto-combined.test/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (path == "/unrelated") { Interlocked.Increment(ref unexpected); await route.FulfillAsync(new() { Status = 204 }); return; }
            await route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = path == "/login"
                    ? """
                      <form id="newsletter"><input id="marketing" type="email"
                        oninput="fetch('/unrelated',{method:'POST'})"></form>
                      <form id="local-login" onsubmit="event.preventDefault();location.href='/dashboard'">
                        <input id="account" type="email">
                        <input id="password" type="password">
                        <button type="submit">Log In</button>
                      </form>
                      """
                    : "<h1 id='signed-in'>Account</h1>"
            });
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://auto-combined.test/login");
        var workflow = Workflow(page.Url);
        await using var engine = new AutomationEngine(Vault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.FinishedCount == 1 && page.Url.EndsWith("/dashboard") && unexpected == 0,
            "AUTO-02 first generic email field in unrelated newsletter cannot receive username");
    }

    private static async Task IdentifierThenPasswordAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        var continuations = 0;
        var finalSubmits = 0;
        await context.RouteAsync("https://auto-staged.test/**", route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            return route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = path switch
                {
                    "/login" => """
                        <form id="identifier" onsubmit="event.preventDefault();location.href='/password'">
                          <input id="login_field" name="login" type="text" autocomplete="username">
                          <button type="submit" onclick="fetch('/continued',{method:'POST'})">Next</button>
                        </form>
                        """,
                    "/password" => """
                        <form id="secret" onsubmit="event.preventDefault();location.href='/dashboard'">
                          <input id="password" type="password" autocomplete="current-password">
                          <button type="submit" onclick="fetch('/submitted',{method:'POST'})">Sign in</button>
                        </form>
                        """,
                    _ => "<h1 id='signed-in'>Authenticated</h1>"
                }
            });
        });
        await context.RouteAsync("https://auto-staged.test/continued", async route => { Interlocked.Increment(ref continuations); await route.FulfillAsync(new() { Status = 204 }); });
        await context.RouteAsync("https://auto-staged.test/submitted", async route => { Interlocked.Increment(ref finalSubmits); await route.FulfillAsync(new() { Status = 204 }); });
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://auto-staged.test/login");
        var workflow = Workflow(page.Url);
        await using var engine = new AutomationEngine(Vault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.FinishedCount == 1 && page.Url.EndsWith("/dashboard") &&
              continuations == 1 && finalSubmits == 1,
            "AUTO-03 identifier stage must continue once then submit the bound password form exactly once");
    }

    private static async Task ResumedPasswordOnlyAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        await context.RouteAsync("https://auto-resumed.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = new Uri(route.Request.Url).AbsolutePath == "/password"
                ? """
                  <form id="login" onsubmit="event.preventDefault();location.href='/dashboard'">
                    <input id="readonly-username" readonly value="test@example.invalid">
                    <input id="password" type="password" autocomplete="current-password">
                    <button type="submit">Sign in</button>
                  </form>
                  """
                : "<h1 id='signed-in'>Dashboard</h1>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://auto-resumed.test/password");
        var workflow = Workflow("https://auto-resumed.test/login");
        var vault = Vault();
        await using var engine = new AutomationEngine(vault);
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.FinishedCount == 1 && result.LoginNeedsAttention.Count == 0 &&
              page.Url.EndsWith("/dashboard") && vault.ReadCount == 1,
            "AUTO-05 current password-only stage can resume without an editable username field");
    }

    private static async Task DuplicateAndAmbiguousEntriesAsync(IPlaywright playwright)
    {
        foreach (var ambiguous in new[] { false, true })
        {
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            await using var context = await browser.NewContextAsync();
            context.SetDefaultTimeout(3000);
            await context.RouteAsync("https://auto-entry.test/**", route =>
                route.FulfillAsync(new()
                {
                    ContentType = "text/html",
                    Body = new Uri(route.Request.Url).AbsolutePath switch
                    {
                        "/" => ambiguous
                            ? "<a href='/login'>Sign in</a><a href='/other-login'>Log In</a>"
                            : "<a href='/login'>Sign in</a><a href='/login'>Log In</a>",
                        "/dashboard" => "<h1 id='signed-in'>Dashboard</h1>",
                        _ => "<form onsubmit=\"event.preventDefault();location.href='/dashboard'\"><input type='email'><input type='password'><button type='submit'>Sign in</button></form>"
                    }
                }));
            var page = await context.NewPageAsync();
            await page.GotoAsync("https://auto-entry.test/");
            var workflow = Workflow(page.Url);
            var vault = Vault();
            await using var engine = new AutomationEngine(vault);
            EngineBehaviorTests.Track(engine, workflow, context, page);
            var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
            if (ambiguous)
                Check(result.LoginNeedsAttention.Count == 1 && page.Url.EndsWith("/") &&
                      vault.ReadCount == 0,
                    "AUTO-10 distinct login-entry destinations must refuse before credential access");
            else
                Check(result.FinishedCount == 1 && page.Url.EndsWith("/dashboard"),
                    "AUTO-09 equivalent responsive login links must be deduplicated");
        }
    }

    private static async Task ExternalFinalFormAssociationAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        await context.RouteAsync("https://auto-associated.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = new Uri(route.Request.Url).AbsolutePath == "/login"
                ? """
                  <form id="login" onsubmit="event.preventDefault();location.href='/dashboard'">
                    <input name="login" autocomplete="username"><input type="password">
                  </form>
                  <input type="submit" form="login" value="Sign in">
                  """
                : "<h1 id='signed-in'>Dashboard</h1>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://auto-associated.test/login");
        var workflow = Workflow(page.Url);
        await using var engine = new AutomationEngine(Vault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.FinishedCount == 1 && page.Url.EndsWith("/dashboard"),
            "AUTO-11 external form= final submit is owned by the filled password field");
    }

    private static async Task UnrelatedFinalSubmitAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        var unrelatedRequests = 0;
        await context.RouteAsync("https://auto-unrelated.test/**", async route =>
        {
            if (new Uri(route.Request.Url).AbsolutePath == "/wrong")
            {
                Interlocked.Increment(ref unrelatedRequests);
                await route.FulfillAsync(new() { Status = 204 });
                return;
            }
            await route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = """
                    <form id="login">
                      <input id="login_field" autocomplete="username">
                      <input id="password" type="password">
                    </form>
                    <form id="unrelated">
                      <input type="password">
                      <button type="submit" onclick="event.preventDefault();fetch('/wrong',{method:'POST'})">Sign in</button>
                    </form>
                    """
            });
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://auto-unrelated.test/login");
        var workflow = Workflow(page.Url);
        await using var engine = new AutomationEngine(Vault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.LoginNeedsAttention.Count == 1 && unrelatedRequests == 0 &&
              await page.GetAttributeAsync("body", "data-clicked") is null,
            "AUTO-12 unrelated password form cannot inherit final submit authorization");
    }

    private static async Task SignupNeverReceivesPasswordAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        await context.RouteAsync("https://auto-signup.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = """
                <form id="register" action="/register">
                  <input id="email" type="email"><input id="new-password" type="password" autocomplete="new-password">
                  <button type="submit">Create account</button>
                </form>
                """
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://auto-signup.test/register");
        var workflow = Workflow(page.Url);
        var vault = Vault();
        await using var engine = new AutomationEngine(vault);
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.LoginNeedsAttention.Count == 1 &&
              await page.InputValueAsync("#email") == "" &&
              await page.InputValueAsync("#new-password") == "" &&
              vault.ReadCount == 0,
            "AUTO-22 sign-up/new-password form must never access saved credentials or fill");
    }

    private static async Task MarketingMfaTextDoesNotBlockAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        await context.RouteAsync("https://auto-marketing.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = new Uri(route.Request.Url).AbsolutePath == "/login"
                ? "<form onsubmit=\"event.preventDefault();location.href='/dashboard'\"><input type='email'><input type='password'><button type='submit'>Sign in</button></form>"
                : "<h1 id='signed-in'>Dashboard</h1><footer>Learn about two-factor authentication and MFA.</footer>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://auto-marketing.test/login");
        var workflow = Workflow(page.Url);
        await using var engine = new AutomationEngine(Vault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.FinishedCount == 1 && result.LoginNeedsAttention.Count == 0 &&
              page.Url.EndsWith("/dashboard"),
            "AUTO-17 passive footer MFA/security education must not be mistaken for active challenge");
    }

    private static async Task UnverifiedRedirectNeverRunsFollowupAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        await context.RouteAsync("https://auto-uncertain.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = new Uri(route.Request.Url).AbsolutePath == "/login"
                ? "<form onsubmit=\"event.preventDefault();location.href='/welcome'\"><input type='email'><input type='password'><button type='submit'>Sign in</button></form>"
                : "<h1>Welcome! Please finish verifying your details.</h1><button id='followup' onclick=\"document.body.dataset.followed='yes'\">Dangerous follow-up</button>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://auto-uncertain.test/login");
        var workflow = Workflow(page.Url);
        workflow.Steps.Add(new WorkflowStep { Type = "Click", Target = "#followup" });
        await using var engine = new AutomationEngine(Vault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.LoginNeedsAttention.Count == 1 && result.FinishedCount == 0 &&
              await page.GetAttributeAsync("body", "data-followed") is null,
            "AUTO-24 URL change and nonempty body never authorize unverified follow-up");
    }

    private static void Check(bool condition, string description) =>
        EngineBehaviorTests.Check(condition, description);
}
