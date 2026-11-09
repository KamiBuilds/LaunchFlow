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
        if (!await PasswordFormsAsync(playwright, false, rememberedCustomSelectors: false, customUsername: false)) failures++;
        if (!await PasswordFormsAsync(playwright, true, rememberedCustomSelectors: false, customUsername: false)) failures++;
        // Remembered CustomSelectors must not bypass classification on default selectors.
        if (!await PasswordFormsAsync(playwright, true, rememberedCustomSelectors: true, customUsername: false)) failures++;
        // Custom username + default password must still classify the password field.
        if (!await PasswordFormsAsync(playwright, false, rememberedCustomSelectors: false, customUsername: true)) failures++;
        if (!await PasswordFormsAsync(playwright, true, rememberedCustomSelectors: false, customUsername: true)) failures++;
        if (!await PasswordFormsAsync(playwright, true, rememberedCustomSelectors: true, customUsername: true)) failures++;
        // Reverse mixed-selector case: the username form is known, but a custom
        // password selector also matches a preceding registration password.
        if (!await PasswordFormsAsync(playwright, false, false, false, customPassword: true)) failures++;
        if (!await PasswordFormsAsync(playwright, true, false, false, customPassword: true)) failures++;
        if (!await PasswordFormsAsync(playwright, true, true, false, customPassword: true)) failures++;
        if (!await PasswordFormsAsync(playwright, true, false, true, customPassword: true)) failures++;
        if (!await PasswordFormsAsync(playwright, true, true, true, customPassword: true)) failures++;
        if (!await ExplicitUniqueNoFormPasswordAsync(playwright)) failures++;
        if (!await ExplicitNoFormSamePageAsync(playwright)) failures++;
        if (!await ExplicitAmbiguousPasswordAsync(playwright)) failures++;
        if (!await HiddenTemplatePasswordAsync(playwright)) failures++;
        if (!await NoOpContinuationDoesNotBindOtherFormAsync(playwright)) failures++;
        if (!await AutomaticUsernameConfiguredStagedPasswordAsync(playwright)) failures++;
        if (!await ExplicitCustomSelectorsAsync(playwright)) failures++;
        if (!await PasswordOnlyCompetingAsync(playwright)) failures++;
        if (!await AmbiguousPasswordAsync(playwright)) failures++;
        if (!await NewsletterAsync(playwright)) failures++;
        Console.WriteLine($"REVIEW_PROBES assertionFailures={failures}");
        if (failures != 0) throw new Exception("Review security assertions failed; inspect per-case evidence. No production code was changed.");
    }
    private static WebsiteWorkflow Workflow(
        string url, bool rememberedCustomSelectors = false, bool customUsername = false,
        bool customPassword = false) => new()
    {
        Name = "Owned synthetic review fixture", StartUrl = url, LoginWaitSeconds = 5,
        LoginSuccessSelector = "", LoginSuccessUrlContains = "",
        UsernameSelector = customUsername ? "#login_field" : LoginFieldDefaults.UsernameSelector,
        PasswordSelector = customPassword ? ".site-password" : LoginFieldDefaults.PasswordSelector,
        LoginRecipe = rememberedCustomSelectors ? LoginRecipe.CustomSelectors : LoginRecipe.Unknown
    };
    private static CredentialVault Vault() => new() { Credential = new SiteCredential(User, Password) };

    private static async Task<bool> PasswordFormsAsync(
        IPlaywright playwright, bool wrongFirst, bool rememberedCustomSelectors, bool customUsername,
        bool customPassword = false)
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
                  <input id="registration-password" class="site-password" type="password" autocomplete="new-password"
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
                      <input id="password" class="site-password" type="password" autocomplete="current-password">
                      <button type="submit">Sign in</button>
                    </form>
                    """ : "<main id='signed-in'>Account dashboard</main>"
            });
        });
        var page = await context.NewPageAsync(); await page.GotoAsync("https://review-forms.test/login");
        var vault = Vault(); var workflow = Workflow(page.Url, rememberedCustomSelectors, customUsername, customPassword);
        await using var engine = new AutomationEngine(vault);
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        var wrongPasswordMatches = page.Url.EndsWith("/login") && wrongFirst &&
            await page.InputValueAsync("#registration-password") == Password;
        Console.WriteLine($"REVIEW_PASSWORD_FORMS wrongFirst={wrongFirst} customUsername={customUsername} customPassword={customPassword} rememberedCustomSelectors={rememberedCustomSelectors} wrongFieldMatchesSyntheticSecret={wrongPasswordMatches} wrongInputRequests={badInputs} wrongSubmitRequests={badSubmits} correctSubmitRequests={goodSubmits} finished={result.FinishedCount} attention={result.LoginNeedsAttention.Count} vaultReads={vault.ReadCount}");
        var ok = badInputs == 0 && badSubmits == 0 && !wrongPasswordMatches && result.FinishedCount == 1 && goodSubmits == 1;
        Console.WriteLine((ok ? "PASS " : "FAIL ") +
            "REVIEW_PASSWORD_FORMS owner-associated password and correct submit; wrongFirst=" +
            wrongFirst + "; customUsername=" + customUsername +
            "; rememberedCustomSelectors=" + rememberedCustomSelectors +
            "; customPassword=" + customPassword);
        return ok;
    }

    private static async Task<bool> ExplicitCustomSelectorsAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        var goodSubmits = 0;
        await context.RouteAsync("https://review-explicit.test/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (path == "/good-submit") { Interlocked.Increment(ref goodSubmits); await route.FulfillAsync(new() { Status = 204 }); return; }
            await route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = path == "/login" ? """
                    <form id="login" onsubmit="event.preventDefault();fetch('/good-submit',{method:'POST'}).then(()=>location.href='/dashboard')">
                      <input id="login_field" autocomplete="username">
                      <input id="site-password" type="password" autocomplete="current-password">
                      <button type="submit">Sign in</button>
                    </form>
                    """ : "<main id='signed-in'>Account dashboard</main>"
            });
        });
        var page = await context.NewPageAsync(); await page.GotoAsync("https://review-explicit.test/login");
        var vault = Vault();
        var workflow = new WebsiteWorkflow
        {
            Name = "Explicit selectors", StartUrl = page.Url, LoginWaitSeconds = 5,
            UsernameSelector = "#login_field", PasswordSelector = "#site-password",
            LoginSuccessSelector = "", LoginSuccessUrlContains = ""
        };
        await using var engine = new AutomationEngine(vault);
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        var ok = result.FinishedCount == 1 && goodSubmits == 1;
        Console.WriteLine((ok ? "PASS " : "FAIL ") +
            "REVIEW_EXPLICIT_CUSTOM_SELECTORS finished=" + result.FinishedCount +
            " correctSubmitRequests=" + goodSubmits);
        return ok;
    }

    private static async Task<bool> ExplicitUniqueNoFormPasswordAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        var submitted = 0;
        await context.RouteAsync("https://review-explicit-noform.test/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (path == "/submitted")
            {
                Interlocked.Increment(ref submitted);
                await route.FulfillAsync(new() { Status = 204 });
                return;
            }
            await route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = path == "/login"
                    ? """
                      <input id="password" type="password" autocomplete="current-password">
                      <button id="submit" onclick="fetch('/submitted',{method:'POST'}).then(()=>location.href='/dashboard')">Sign in</button>
                      """
                    : "<main id='signed-in'>Dashboard</main>"
            });
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://review-explicit-noform.test/login");
        var workflow = new WebsiteWorkflow
        {
            Name = "Unique no-form password", StartUrl = page.Url, LoginWaitSeconds = 5,
            UsernameSelector = "", PasswordSelector = "#password", SubmitSelector = "#submit",
            LoginSuccessSelector = "", LoginSuccessUrlContains = ""
        };
        await using var engine = new AutomationEngine(Vault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        var ok = result.FinishedCount == 1 && result.LoginNeedsAttention.Count == 0 &&
            submitted == 1 && page.Url.EndsWith("/dashboard", StringComparison.Ordinal);
        Console.WriteLine((ok ? "PASS " : "FAIL ") +
            "REVIEW_EXPLICIT_UNIQUE_NOFORM finished=" + result.FinishedCount + " submitted=" + submitted);
        return ok;
    }

    private static async Task<bool> ExplicitNoFormSamePageAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        var wrongInputs = 0; var correctSubmits = 0;
        await context.RouteAsync("https://review-explicit-spa.test/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (path == "/wrong-input") { Interlocked.Increment(ref wrongInputs); await route.FulfillAsync(new() { Status = 204 }); return; }
            if (path == "/submitted") { Interlocked.Increment(ref correctSubmits); await route.FulfillAsync(new() { Status = 204 }); return; }
            await route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = path == "/login" ? """
                    <input id="email" autocomplete="username">
                    <input id="password" type="password" autocomplete="current-password">
                    <button id="submit" onclick="fetch('/submitted',{method:'POST'}).then(()=>location.href='/dashboard')">Sign in</button>
                    """ : "<main id='signed-in'>Dashboard</main>"
            });
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://review-explicit-spa.test/login");
        var workflow = new WebsiteWorkflow
        {
            Name = "Explicit same-document SPA", StartUrl = page.Url, LoginWaitSeconds = 5,
            UsernameSelector = "#email", PasswordSelector = "#password", SubmitSelector = "#submit",
            LoginSuccessSelector = "", LoginSuccessUrlContains = ""
        };
        await using var engine = new AutomationEngine(Vault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        var ok = result.FinishedCount == 1 && result.LoginNeedsAttention.Count == 0 &&
            wrongInputs == 0 && correctSubmits == 1;
        Console.WriteLine((ok ? "PASS " : "FAIL ") +
            "REVIEW_EXPLICIT_SAME_DOCUMENT finished=" + result.FinishedCount + " submitted=" + correctSubmits);
        return ok;
    }

    private static async Task<bool> ExplicitAmbiguousPasswordAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        var wrongInputs = 0; var rightInputs = 0;
        await context.RouteAsync("https://review-explicit-ambiguous.test/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (path == "/wrong-input") { Interlocked.Increment(ref wrongInputs); await route.FulfillAsync(new() { Status = 204 }); return; }
            if (path == "/right-input") { Interlocked.Increment(ref rightInputs); await route.FulfillAsync(new() { Status = 204 }); return; }
            await route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = """
                    <form id="register"><input class="site-password" id="registration-password" type="password" autocomplete="new-password"
                      oninput="fetch('/wrong-input',{method:'POST'})"></form>
                    <form id="login"><input class="site-password" id="login-password" type="password" autocomplete="current-password"
                      oninput="fetch('/right-input',{method:'POST'})"><button type="submit">Sign in</button></form>
                    """
            });
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://review-explicit-ambiguous.test/login");
        var workflow = new WebsiteWorkflow
        {
            Name = "Ambiguous explicit password", StartUrl = page.Url, LoginWaitSeconds = 5,
            UsernameSelector = "", PasswordSelector = ".site-password",
            LoginSuccessSelector = "", LoginSuccessUrlContains = ""
        };
        await using var engine = new AutomationEngine(Vault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        var ok = result.FinishedCount == 0 && result.LoginNeedsAttention.Count == 1 &&
            wrongInputs == 0 && rightInputs == 0 &&
            await page.InputValueAsync("#registration-password") == "" &&
            await page.InputValueAsync("#login-password") == "";
        Console.WriteLine((ok ? "PASS " : "FAIL ") +
            "REVIEW_EXPLICIT_AMBIGUITY no secret inserted; wrong=" + wrongInputs + " right=" + rightInputs);
        return ok;
    }

    private static async Task<bool> HiddenTemplatePasswordAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        var wrongInputs = 0; var correctSubmits = 0;
        await context.RouteAsync("https://review-hidden-template.test/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (path == "/wrong-input") { Interlocked.Increment(ref wrongInputs); await route.FulfillAsync(new() { Status = 204 }); return; }
            if (path == "/submitted") { Interlocked.Increment(ref correctSubmits); await route.FulfillAsync(new() { Status = 204 }); return; }
            await route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = path == "/login" ? """
                    <form id="login" onsubmit="event.preventDefault();fetch('/submitted',{method:'POST'}).then(()=>location.href='/dashboard')">
                      <input id="email" autocomplete="username">
                      <input class="site-password" id="template-password" type="password" style="display:none"
                        oninput="fetch('/wrong-input',{method:'POST'})">
                      <input class="site-password" id="login-password" type="password" autocomplete="current-password">
                      <button type="submit">Sign in</button>
                    </form>
                    """ : "<main id='signed-in'>Dashboard</main>"
            });
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://review-hidden-template.test/login");
        var workflow = new WebsiteWorkflow
        {
            Name = "Hidden template password", StartUrl = page.Url, LoginWaitSeconds = 5,
            UsernameSelector = "#email", PasswordSelector = ".site-password",
            LoginSuccessSelector = "", LoginSuccessUrlContains = ""
        };
        await using var engine = new AutomationEngine(Vault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        var ok = result.FinishedCount == 1 && result.LoginNeedsAttention.Count == 0 &&
            correctSubmits == 1 && wrongInputs == 0;
        Console.WriteLine((ok ? "PASS " : "FAIL ") +
            "REVIEW_HIDDEN_TEMPLATE password-owned; wrong=" + wrongInputs + " submitted=" + correctSubmits);
        return ok;
    }

    private static async Task<bool> NoOpContinuationDoesNotBindOtherFormAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        var wrongInputs = 0;
        await context.RouteAsync("https://review-noop-next.test/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (path == "/wrong-input") { Interlocked.Increment(ref wrongInputs); await route.FulfillAsync(new() { Status = 204 }); return; }
            await route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = """
                    <form id="login" onsubmit="event.preventDefault()">
                      <input id="email" autocomplete="username">
                      <button type="submit">Next</button>
                    </form>
                    <form id="other-login" onsubmit="event.preventDefault()">
                      <input class="site-password" id="unrelated-password" type="password" autocomplete="current-password"
                        oninput="fetch('/wrong-input',{method:'POST'})">
                      <button type="submit">Sign in</button>
                    </form>
                    """
            });
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://review-noop-next.test/login");
        var workflow = new WebsiteWorkflow
        {
            Name = "No-op continuation must not change owner", StartUrl = page.Url, LoginWaitSeconds = 3,
            UsernameSelector = "#email", PasswordSelector = ".site-password",
            LoginSuccessSelector = "", LoginSuccessUrlContains = ""
        };
        await using var engine = new AutomationEngine(Vault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        var ok = result.FinishedCount == 0 && result.LoginNeedsAttention.Count == 1 &&
            wrongInputs == 0 && await page.InputValueAsync("#unrelated-password") == "";
        Console.WriteLine((ok ? "PASS " : "FAIL ") +
            "REVIEW_NOOP_STAGE no unowned password insertion; wrong=" + wrongInputs);
        return ok;
    }

    private static async Task<bool> AutomaticUsernameConfiguredStagedPasswordAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        var correctSubmits = 0;
        await context.RouteAsync("https://review-mixed-staged.test/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (path == "/submitted") { Interlocked.Increment(ref correctSubmits); await route.FulfillAsync(new() { Status = 204 }); return; }
            await route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = path == "/login" ? """
                    <form id="login" onsubmit="event.preventDefault();location.href='/password'">
                      <input id="login_field" autocomplete="username">
                      <button type="submit">Next</button>
                    </form>
                    """ : path == "/password" ? """
                    <form id="login" onsubmit="event.preventDefault();fetch('/submitted',{method:'POST'}).then(()=>location.href='/dashboard')">
                      <input id="site-password" type="password" autocomplete="current-password">
                      <button type="submit">Sign in</button>
                    </form>
                    """ : "<main id='signed-in'>Dashboard</main>"
            });
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://review-mixed-staged.test/login");
        var workflow = new WebsiteWorkflow
        {
            Name = "Mixed stage automatic username", StartUrl = page.Url, LoginWaitSeconds = 5,
            UsernameSelector = LoginFieldDefaults.UsernameSelector, PasswordSelector = "#site-password",
            LoginSuccessSelector = "", LoginSuccessUrlContains = ""
        };
        await using var engine = new AutomationEngine(Vault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        var ok = result.FinishedCount == 1 && result.LoginNeedsAttention.Count == 0 &&
            correctSubmits == 1 && page.Url.EndsWith("/dashboard", StringComparison.Ordinal);
        Console.WriteLine((ok ? "PASS " : "FAIL ") +
            "REVIEW_MIXED_STAGED submitted=" + correctSubmits + " finished=" + result.FinishedCount);
        return ok;
    }

    private static async Task<bool> PasswordOnlyCompetingAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        var badInputs = 0; var goodSubmits = 0;
        await context.RouteAsync("https://review-password-only.test/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (path == "/bad-input") { Interlocked.Increment(ref badInputs); await route.FulfillAsync(new() { Status = 204 }); return; }
            if (path == "/good-submit") { Interlocked.Increment(ref goodSubmits); await route.FulfillAsync(new() { Status = 204 }); return; }
            await route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = path == "/login" ? """
                    <form id="register" onsubmit="event.preventDefault()">
                      <input id="registration-password" type="password" autocomplete="new-password"
                        oninput="fetch('/bad-input',{method:'POST'})">
                    </form>
                    <form id="login" action="/session" onsubmit="event.preventDefault();fetch('/good-submit',{method:'POST'}).then(()=>location.href='/dashboard')">
                      <input id="password" type="password" autocomplete="current-password">
                      <button type="submit">Sign in</button>
                    </form>
                    """ : "<main id='signed-in'>Account dashboard</main>"
            });
        });
        var page = await context.NewPageAsync(); await page.GotoAsync("https://review-password-only.test/login");
        var vault = Vault();
        var workflow = new WebsiteWorkflow
        {
            Name = "Password-only discovery", StartUrl = page.Url, LoginWaitSeconds = 5,
            UsernameSelector = "", PasswordSelector = LoginFieldDefaults.PasswordSelector,
            LoginSuccessSelector = "", LoginSuccessUrlContains = ""
        };
        await using var engine = new AutomationEngine(vault);
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        var wrongPasswordMatches = page.Url.EndsWith("/login") &&
            await page.InputValueAsync("#registration-password") == Password;
        var ok = badInputs == 0 && !wrongPasswordMatches && result.FinishedCount == 1 && goodSubmits == 1;
        Console.WriteLine((ok ? "PASS " : "FAIL ") +
            "REVIEW_PASSWORD_ONLY_COMPETING wrongFieldMatchesSyntheticSecret=" + wrongPasswordMatches +
            " wrongInputRequests=" + badInputs + " correctSubmitRequests=" + goodSubmits +
            " finished=" + result.FinishedCount + " attention=" + result.LoginNeedsAttention.Count);
        return ok;
    }

    private static async Task<bool> AmbiguousPasswordAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        var anyInputs = 0;
        await context.RouteAsync("https://review-ambiguous.test/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (path == "/any-input") { Interlocked.Increment(ref anyInputs); await route.FulfillAsync(new() { Status = 204 }); return; }
            await route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = """
                    <form id="login-a" action="/a">
                      <input id="password-a" type="password" autocomplete="current-password"
                        oninput="fetch('/any-input',{method:'POST'})">
                      <button type="submit">Sign in A</button>
                    </form>
                    <form id="login-b" action="/b">
                      <input id="password-b" type="password" autocomplete="current-password"
                        oninput="fetch('/any-input',{method:'POST'})">
                      <button type="submit">Sign in B</button>
                    </form>
                    """
            });
        });
        var page = await context.NewPageAsync(); await page.GotoAsync("https://review-ambiguous.test/login");
        var vault = Vault();
        var workflow = new WebsiteWorkflow
        {
            Name = "Ambiguous passwords", StartUrl = page.Url, LoginWaitSeconds = 5,
            UsernameSelector = "", PasswordSelector = LoginFieldDefaults.PasswordSelector,
            LoginSuccessSelector = "", LoginSuccessUrlContains = ""
        };
        await using var engine = new AutomationEngine(vault);
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        var filledA = await page.InputValueAsync("#password-a") == Password;
        var filledB = await page.InputValueAsync("#password-b") == Password;
        var ok = anyInputs == 0 && !filledA && !filledB && result.FinishedCount == 0 &&
            result.LoginNeedsAttention.Count == 1;
        Console.WriteLine((ok ? "PASS " : "FAIL ") +
            "REVIEW_AMBIGUOUS_PASSWORD noSecretInsertion inputEvents=" + anyInputs +
            " filledA=" + filledA + " filledB=" + filledB +
            " finished=" + result.FinishedCount + " attention=" + result.LoginNeedsAttention.Count);
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
