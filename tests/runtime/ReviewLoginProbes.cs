using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;

internal static class ReviewLoginProbes
{
    private const string User = "probe@example.invalid";
    private const string Password = "synthetic-review-password";
    public static async Task RunAsync(IPlaywright playwright)
    {
        var failures = 0;
        if (!await PasswordFormsAsync(playwright, false)) failures++;
        if (!await PasswordFormsAsync(playwright, true)) failures++;
        if (!await NewsletterAsync(playwright)) failures++;
        Console.WriteLine($"REVIEW_PROBES assertionFailures={failures}");
        if (failures != 0) throw new Exception("Review security assertions failed; inspect per-case evidence. No production code was changed.");
    }
    private static WebsiteWorkflow Workflow(string url) => new()
    {
        Name = "Owned synthetic review fixture", StartUrl = url, LoginWaitSeconds = 5,
        LoginSuccessSelector = "", LoginSuccessUrlContains = ""
    };
    private static CredentialVault Vault() => new() { Credential = new SiteCredential(User, Password) };

    private static async Task<bool> PasswordFormsAsync(IPlaywright playwright, bool wrongFirst)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        var badInputs = 0; var badSubmits = 0; var goodSubmits = 0;
        await context.RouteAsync("https://review-forms.test/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (path == "/bad-input") { Interlocked.Increment(ref badInputs); await route.FulfillAsync(new() { Status = 204 }); return; }
            if (path == "/bad-submit") { Interlocked.Increment(ref badSubmits); await route.FulfillAsync(new() { Status = 204 }); return; }
            if (path == "/good-submit") { Interlocked.Increment(ref goodSubmits); await route.FulfillAsync(new() { Status = 204 }); return; }
            var wrong = wrongFirst ? """
                <form id="register" onsubmit="event.preventDefault();fetch('/bad-submit',{method:'POST'})">
                  <input id="registration-password" type="password" autocomplete="new-password"
                    oninput="fetch('/bad-input',{method:'POST'})">
                  <button type="submit">Create account</button>
                </form>
                """ : "";
            await route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = path == "/login" ? wrong + """
                    <form id="login" action="/session" onsubmit="event.preventDefault();fetch('/good-submit',{method:'POST'}).then(()=>location.href='/dashboard')">
                      <input id="login_field" name="login" autocomplete="username">
                      <input id="password" type="password" autocomplete="current-password">
                      <button type="submit">Sign in</button>
                    </form>
                    """ : "<main id='signed-in'>Account dashboard</main>"
            });
        });
        var page = await context.NewPageAsync(); await page.GotoAsync("https://review-forms.test/login");
        var vault = Vault(); var workflow = Workflow(page.Url);
        await using var engine = new AutomationEngine(vault);
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        var wrongPasswordMatches = page.Url.EndsWith("/login") && wrongFirst &&
            await page.InputValueAsync("#registration-password") == Password;
        Console.WriteLine($"REVIEW_PASSWORD_FORMS wrongFirst={wrongFirst} wrongFieldMatchesSyntheticSecret={wrongPasswordMatches} wrongInputRequests={badInputs} wrongSubmitRequests={badSubmits} correctSubmitRequests={goodSubmits} finished={result.FinishedCount} attention={result.LoginNeedsAttention.Count} vaultReads={vault.ReadCount}");
        var ok = badInputs == 0 && badSubmits == 0 && !wrongPasswordMatches && result.FinishedCount == 1 && goodSubmits == 1;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + "REVIEW_PASSWORD_FORMS owner-associated password and correct submit; wrongFirst=" + wrongFirst);
        return ok;
    }

    private static async Task<bool> NewsletterAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(); context.SetDefaultTimeout(3000);
        var newsletterInputs = 0; var newsletterSubmits = 0; var loginVisits = 0;
        await context.RouteAsync("https://review-newsletter.test/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (path == "/newsletter-input") { Interlocked.Increment(ref newsletterInputs); await route.FulfillAsync(new() { Status = 204 }); return; }
            if (path == "/newsletter-submit") { Interlocked.Increment(ref newsletterSubmits); await route.FulfillAsync(new() { Status = 204 }); return; }
            if (path == "/login") Interlocked.Increment(ref loginVisits);
            await route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = path switch
                {
                    "/" => """
                        <form id="newsletter" onsubmit="event.preventDefault();fetch('/newsletter-submit',{method:'POST'})">
                          <input id="marketing" type="email" oninput="fetch('/newsletter-input',{method:'POST'})">
                          <button type="submit">Continue</button>
                        </form>
                        <nav><a href="/login">Sign in</a></nav>
                        """,
                    "/login" => """
                        <form id="login" onsubmit="event.preventDefault();location.href='/dashboard'">
                          <input id="login_field" name="login" autocomplete="username">
                          <input id="password" type="password" autocomplete="current-password">
                          <input type="submit" value="Sign in">
                        </form>
                        """,
                    _ => "<main id='signed-in'>Account dashboard</main>"
                }
            });
        });
        var page = await context.NewPageAsync(); await page.GotoAsync("https://review-newsletter.test/");
        var vault = Vault(); var workflow = Workflow(page.Url);
        await using var engine = new AutomationEngine(vault); EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        var usernameInNewsletter = page.Url == "https://review-newsletter.test/" && await page.InputValueAsync("#marketing") == User;
        Console.WriteLine($"REVIEW_NEWSLETTER usernameInNewsletter={usernameInNewsletter} newsletterInputRequests={newsletterInputs} newsletterSubmitRequests={newsletterSubmits} loginVisits={loginVisits} finished={result.FinishedCount} attention={result.LoginNeedsAttention.Count} vaultReads={vault.ReadCount}");
        var ok = !usernameInNewsletter && newsletterInputs == 0 && newsletterSubmits == 0 && loginVisits == 1 && result.FinishedCount == 1;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + "REVIEW_NEWSLETTER authentication intent must precede form ownership");
        return ok;
    }
}
