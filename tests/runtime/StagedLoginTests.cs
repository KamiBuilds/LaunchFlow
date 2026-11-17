using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;

internal static class StagedLoginTests
{
    public static async Task RunAsync(IPlaywright playwright)
    {
        await SinglePageLoginAsync(playwright);
        await LegacyNpmWithoutAdvancedSettingsAsync(playwright);
        await EmailThenPasswordAsync(playwright);
        await IdBrokerStyleContinuationAsync(playwright);
        await DifferentFinalSubmitAsync(playwright);
        await ResumeAtPasswordAsync(playwright);
        await AmbiguousFinalSubmitAsync(playwright);
        await UnapprovedIdpAsync(playwright);
        await MfaRequiresHumanAsync(playwright);
        await AmbiguousButtonDoesNotGuessAsync(playwright);
        await UnrelatedFirstStageButtonDoesNotDispatchAsync(playwright);
        await OwnedFirstStageButtonIgnoresOtherFormAsync(playwright);
        await ExplicitFormAssociatedContinuationAsync(playwright);
        await ExplicitNonFormContinuationAsync(playwright);

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


    private static async Task SinglePageLoginAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        await context.RouteAsync("https://singlepage.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = route.Request.Url.EndsWith("/login", StringComparison.Ordinal)
                ? "<form onsubmit=\"event.preventDefault();location.href='/dashboard'\"><input id='email' type='email'><input id='secret' type='password'><button type='submit'>Sign in</button></form>"
                : "<h1 id='signed-in'>Signed in</h1>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://singlepage.test/login");
        var workflow = NewWorkflow("https://singlepage.test/login");
        var vault = NewVault();
        await using var engine = new AutomationEngine(vault);
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Console.WriteLine($"Single-page fixture: finished={result.FinishedCount}, attention={result.LoginNeedsAttention.Count}, path={new Uri(page.Url).AbsolutePath}, vaultReads={vault.ReadCount}");
        Check(result.FinishedCount == 1 && result.LoginNeedsAttention.Count == 0 &&
              page.Url.EndsWith("/dashboard") && vault.ReadCount == 1,
              "single-page username + password submission still works without recording");
    }

    private static async Task LegacyNpmWithoutAdvancedSettingsAsync(IPlaywright playwright)
    {
        const string origin = "https://npm-legacy.test";
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        await context.RouteAsync(origin + "/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = route.Request.Url.EndsWith("/login", StringComparison.Ordinal)
                ? """
                  <form action="" method="post"
                    onsubmit="event.preventDefault(); if (this.elements.identity.value === 'test@example.invalid' && this.elements.secret.value === 'test-only-password') location.href='/'; else document.getElementById('error').textContent='Invalid credentials';">
                    <input name="identity" type="email" required>
                    <input name="secret" type="password" required>
                    <button type="submit">Sign in</button>
                    <p id="error"></p>
                  </form>
                  """
                : """
                  <header>
                    <a class="navbar-brand" href="/"><img src="/images/favicons/favicon-32x32.png">Nginx Proxy Manager</a>
                    <div class="dropdown">
                      <a href="#" class="nav-link"><span class="avatar"></span><span class="text-default">Admin</span></a>
                      <div class="dropdown-menu" style="display:none">
                        <a class="dropdown-item logout" href="/logout">Log out</a>
                      </div>
                    </div>
                  </header>
                  <main>Proxy Hosts</main>
                  """
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync(origin + "/login");
        var workflow = new WebsiteWorkflow { StartUrl = origin + "/login", LoginWaitSeconds = 5 };
        var vault = new CredentialVault
        {
            Credential = new SiteCredential("test@example.invalid", "test-only-password")
        };
        await using var engine = new AutomationEngine(vault);
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.FinishedCount == 1 && result.LoginNeedsAttention.Count == 0 &&
              page.Url == origin + "/" && vault.ReadCount == 1,
              "NPM legacy identity/secret login completes with default selectors");
        Check(!await page.Locator("a.dropdown-item.logout").IsVisibleAsync() &&
              await LoginHealth.VerifiedApplicationSessionAsync(page, workflow.StartUrl),
              "NPM dashboard verifies while account logout is hidden in dropdown");
        var again = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(again.FinishedCount == 1 && again.LoginNeedsAttention.Count == 0 &&
              vault.ReadCount == 1,
              "NPM signed-in browser is reused without inserting credentials again");

        await page.GotoAsync(origin + "/login");
        Check(!await LoginHealth.VerifiedApplicationSessionAsync(page, workflow.StartUrl),
              "NPM email/password screen cannot be accepted as authenticated");
        await page.SetContentAsync("""
            <header><a class="navbar-brand" href="/"><img src="/images/favicons/favicon-32x32.png"></a>
              <div class="dropdown"><a class="nav-link" href="#"><span class="avatar"></span>Admin</a>
              <a class="dropdown-item logout" href="/logout">Log out</a></div></header>
            <h2>Verification required</h2><input autocomplete="one-time-code">
            """);
        Check(!await LoginHealth.VerifiedApplicationSessionAsync(page, workflow.StartUrl),
              "NPM-shaped verification challenge never counts as a signed-in session");
    }

    private static async Task DifferentFinalSubmitAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        await context.RouteAsync("https://two-buttons.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = route.Request.Url.EndsWith("/login", StringComparison.Ordinal)
                ? "<form onsubmit=\"event.preventDefault();location.href='/password'\"><input id='email' type='email'><button type='submit'>Next</button></form>"
                : route.Request.Url.EndsWith("/password", StringComparison.Ordinal)
                ? "<form><input id='secret' type='password'><button id='Button1' type='button' onclick=\"location.href='/dashboard'\">Sign in</button></form>"
                : "<h1 id='signed-in'>Dashboard</h1>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://two-buttons.test/login");
        var workflow = NewWorkflow("https://two-buttons.test/login");
        await using var engine = new AutomationEngine(NewVault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.FinishedCount == 1 && result.LoginNeedsAttention.Count == 0 &&
              page.Url.EndsWith("/dashboard"),
              "different final button ID/type is handled automatically with no recording");
    }

    private static async Task ResumeAtPasswordAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        await context.RouteAsync("https://resume-stage.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = route.Request.Url.EndsWith("/password", StringComparison.Ordinal)
                ? "<form onsubmit=\"event.preventDefault();location.href='/dashboard'\"><input id='email' readonly value='user@test.invalid'><input id='secret' type='password'><button type='submit'>Log In</button></form>"
                : "<h1 id='signed-in'>Dashboard</h1>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://resume-stage.test/password");
        var workflow = NewWorkflow("https://resume-stage.test/login");
        await using var engine = new AutomationEngine(NewVault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.FinishedCount == 1 && result.LoginNeedsAttention.Count == 0 &&
              page.Url.EndsWith("/dashboard"),
              "reuses a current password-only stage and ignores read-only email field");
    }

    private static async Task AmbiguousFinalSubmitAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        await context.RouteAsync("https://ambiguous-final.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = "<form><input id='secret' type='password'><button type='button' onclick=\"document.body.dataset.clicked='one'\">Log In</button><button type='button' onclick=\"document.body.dataset.clicked='two'\">Sign in</button></form>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://ambiguous-final.test/password");
        var workflow = NewWorkflow("https://ambiguous-final.test/login");
        workflow.LoginWaitSeconds = 5;
        await using var engine = new AutomationEngine(NewVault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.LoginNeedsAttention.Count == 1 &&
              await page.GetAttributeAsync("body", "data-clicked") is null,
              "ambiguous password submit buttons require manual choice and are never guessed");
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
        var vault = NewVault();
        var workflow = NewWorkflow(page.Url);
        await using var engine = new AutomationEngine(vault);
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Console.WriteLine($"STAGED_EXISTING_TWO_PAGE finished={result.FinishedCount} attention={result.LoginNeedsAttention.Count} path={new Uri(page.Url).AbsolutePath} reads={vault.ReadCount}");
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
                : "<main id='signed-in'>Dashboard</main>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://first.test/login");
        var workflow = NewWorkflow(page.Url);
        workflow.PasswordCredentialOrigin = "https://identity.test";
        var vault = NewVault();
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

        // Approving the IdP during password fill maps PasswordCredentialOrigin
        // (email → company SSO / Webex IdBroker style). Refusal still blocks.
        engine.ApproveLoginOriginAsync = (_, origin) =>
            Task.FromResult(origin == "https://identity.test");
        workflow.PasswordCredentialOrigin = "";
        var mappedByApproval = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(mappedByApproval.FinishedCount == 1 && mappedByApproval.LoginNeedsAttention.Count == 0 &&
              page.Url == "https://identity.test/dashboard" &&
              workflow.PasswordCredentialOrigin.Contains("identity.test", StringComparison.OrdinalIgnoreCase),
              "approving the IdP password host maps it and allows one owned password fill");
    }

    private static async Task IdBrokerStyleContinuationAsync(IPlaywright playwright)
    {
        // Generic email-on-app → password-on-broker host (no product hardcoding).
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(4000);
        await context.RouteAsync("https://app.example-saas.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = """
                <form id="ssoForm" action="/login" method="POST">
                  <input id="email" name="email" type="text" placeholder="Email">
                  <button type="button" id="submit-button"
                    onclick="location.href='https://idbroker.example-idp.test/idb/saml2/jsp/doSSO.jsp'">Log In</button>
                </form>
                """
        }));
        await context.RouteAsync("https://idbroker.example-idp.test/**", route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            return route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = path.EndsWith("/done", StringComparison.Ordinal)
                    ? "<main id='signed-in'>Signed in</main>"
                    : """
                        <form id="login" action="/signin" method="POST">
                          <input id="password" name="password" type="password" placeholder="Password" autocomplete="">
                          <button type="button" id="submit-button"
                            onclick="sessionStorage.setItem('ok',document.getElementById('password').value);location.href='/done'">Log In</button>
                        </form>
                        """
            });
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://app.example-saas.test/login");
        var workflow = NewWorkflow(page.Url);
        workflow.PasswordSelector = LoginFieldDefaults.PasswordSelector;
        workflow.UsernameSelector = LoginFieldDefaults.UsernameSelector;
        workflow.SubmitSelector = LoginFieldDefaults.SubmitSelector;
        workflow.LoginSuccessSelector = "#signed-in";
        var vault = NewVault();
        await using var engine = new AutomationEngine(vault);
        EngineBehaviorTests.Track(engine, workflow, context, page);
        engine.ApproveLoginOriginAsync = (_, origin) =>
            Task.FromResult(origin == "https://idbroker.example-idp.test");
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        var filled = await page.EvaluateAsync<string>("() => sessionStorage.getItem('ok')");
        Check(result.FinishedCount == 1 && filled == "test-only-password" &&
              workflow.PasswordCredentialOrigin.Contains("idbroker.example-idp.test", StringComparison.Ordinal),
              "generic email→IdBroker password continuation after approval");
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
        await using var engine = new AutomationEngine(NewVault());
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
        await using var engine = new AutomationEngine(NewVault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.LoginNeedsAttention.Count == 1 &&
              await page.GetAttributeAsync("body", "data-clicked") is null,
              "ambiguous local-login buttons are not clicked blindly");
    }

    private static async Task UnrelatedFirstStageButtonDoesNotDispatchAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        var unexpectedRequests = 0;
        await context.RouteAsync("https://unrelated-form.test/**", async route =>
        {
            if (route.Request.Url.EndsWith("/unrelated-action", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref unexpectedRequests);
                await route.FulfillAsync(new() { Status = 204 });
                return;
            }
            await route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = """
                    <form id="login">
                      <input id="email" type="email" autocomplete="username">
                    </form>
                    <form id="other">
                      <input id="search" type="search">
                      <button id="other-next" type="button"
                        onclick="document.body.dataset.unrelatedClick='yes';fetch('/unrelated-action',{method:'POST'})">
                        Next
                      </button>
                    </form>
                    """
            });
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://unrelated-form.test/login");
        var workflow = NewWorkflow(page.Url);
        workflow.LoginWaitSeconds = 5;
        var vault = NewVault();
        await using var engine = new AutomationEngine(vault);
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        var unrelatedClick = await page.GetAttributeAsync("body", "data-unrelated-click");
        await page.WaitForTimeoutAsync(200);
        var requests = Volatile.Read(ref unexpectedRequests);
        Console.WriteLine($"WEB-01 two-form fixture: unrelatedClick={unrelatedClick ?? "none"}, unrelatedRequests={requests}, needsAttention={result.LoginNeedsAttention.Count}");
        Check(unrelatedClick is null && requests == 0,
            "WEB-01 unrelated form button must not receive an automatic login click/request");
    }

    private static async Task OwnedFirstStageButtonIgnoresOtherFormAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        var unrelatedRequests = 0;
        await context.RouteAsync("https://owned-stage.test/**", async route =>
        {
            var uri = new Uri(route.Request.Url);
            if (uri.AbsolutePath == "/unrelated-action")
            {
                Interlocked.Increment(ref unrelatedRequests);
                await route.FulfillAsync(new() { Status = 204 });
                return;
            }
            await route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = uri.AbsolutePath == "/login"
                    ? """
                      <form id="login">
                        <input id="email" type="email">
                        <button id="owned-next" type="button"
                          onclick="location.href='/password'">Next</button>
                      </form>
                      <form id="unrelated">
                        <button id="unrelated-next" type="button"
                          onclick="document.body.dataset.unrelatedClick='yes';fetch('/unrelated-action',{method:'POST'})">Next</button>
                      </form>
                      """
                    : uri.AbsolutePath == "/password"
                    ? """
                      <form onsubmit="event.preventDefault();location.href='/dashboard'">
                        <input id="secret" type="password">
                        <button type="submit">Sign in</button>
                      </form>
                      """
                    : "<h1 id='signed-in'>Dashboard</h1>"
            });
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://owned-stage.test/login");
        var workflow = NewWorkflow(page.Url);
        await using var engine = new AutomationEngine(NewVault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.FinishedCount == 1 && result.LoginNeedsAttention.Count == 0 &&
              page.Url.EndsWith("/dashboard", StringComparison.Ordinal) &&
              Volatile.Read(ref unrelatedRequests) == 0,
              "FORM-02 valid same-form Next is selected instead of unrelated form's Next");
        Console.WriteLine("PASS FORM-02 owned first-stage Next; unrelatedRequests=0");
    }

    private static async Task ExplicitFormAssociatedContinuationAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        await context.RouteAsync("https://form-associated.test/**", route =>
        {
            var uri = new Uri(route.Request.Url);
            return route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = uri.AbsolutePath == "/login"
                    ? """
                      <form id="login" onsubmit="event.preventDefault();location.href='/password'">
                        <input id="email" type="email">
                      </form>
                      <button id="external-next" type="submit" form="login">Next</button>
                      """
                    : uri.AbsolutePath == "/password"
                    ? """
                      <form onsubmit="event.preventDefault();location.href='/dashboard'">
                        <input id="secret" type="password">
                        <button type="submit">Sign in</button>
                      </form>
                      """
                    : "<h1 id='signed-in'>Dashboard</h1>"
            });
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://form-associated.test/login");
        var workflow = NewWorkflow(page.Url);
        await using var engine = new AutomationEngine(NewVault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.FinishedCount == 1 && result.LoginNeedsAttention.Count == 0 &&
              page.Url.EndsWith("/dashboard", StringComparison.Ordinal),
              "FORM-03 form= associated external Next button remains a valid login continuation");
        Console.WriteLine("PASS FORM-03 external form-associated Next");
    }

    private static async Task ExplicitNonFormContinuationAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3000);
        await context.RouteAsync("https://explicit-spa.test/**", route =>
        {
            var uri = new Uri(route.Request.Url);
            return route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = uri.AbsolutePath == "/login"
                    ? """
                      <main>
                        <input id="email" type="email">
                        <button id="explicit-next" type="button"
                          onclick="location.href='/password'">Continue</button>
                      </main>
                      """
                    : uri.AbsolutePath == "/password"
                    ? """
                      <form onsubmit="event.preventDefault();location.href='/dashboard'">
                        <input id="secret" type="password">
                        <button type="submit">Sign in</button>
                      </form>
                      """
                    : "<h1 id='signed-in'>Dashboard</h1>"
            });
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://explicit-spa.test/login");
        var workflow = NewWorkflow(page.Url);
        workflow.UsernameContinueSelector = "#explicit-next";
        await using var engine = new AutomationEngine(NewVault());
        EngineBehaviorTests.Track(engine, workflow, context, page);
        var result = await engine.RunAsync(new[] { workflow }, CancellationToken.None);
        Check(result.FinishedCount == 1 && result.LoginNeedsAttention.Count == 0 &&
              page.Url.EndsWith("/dashboard", StringComparison.Ordinal),
              "FORM-10 explicit no-form SPA continuation retains supported functionality");
        Console.WriteLine("PASS FORM-10 explicit no-form SPA continuation");
    }

    // Valid synthetic email avoids the browser's native type=email form
    // validation blocking submission before the engine can advance.
    private static CredentialVault NewVault() => new()
    {
        Credential = new SiteCredential("test@example.invalid", "test-only-password")
    };

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
