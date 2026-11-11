using System.Reflection;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;

internal static class DocumentBoundCredentialTests
{
    private static int _checks;

    public static async Task RunAsync(IPlaywright playwright, string[] args)
    {
        if (args.Contains("--profile-only")) return;
        var tests = new List<(string Name, Func<IBrowser, Task> Run)>
        {
            ("delayed-password", DelayedPasswordAsync),
            ("spa-rendered-login", SpaRenderedLoginAsync),
            ("hidden-first-login-controls", HiddenFirstLoginAsync),
            ("missing-required-password", MissingRequiredPasswordAsync),
            ("two-stage-js-password", TwoStageJavaScriptPasswordAsync),
            ("existing-signed-in-session", ExistingSignedInSessionAsync)
        };
        foreach (var field in new[] { "user", "password" })
        {
            var target = field;
            tests.Add(($"waiting-navigation-{target}", browser => WaitingNavigationAsync(browser, target)));
            tests.Add(($"approval-navigation-{target}", browser => ApprovalNavigationAsync(browser, target, false)));
            tests.Add(($"approval-replacement-{target}", browser => ApprovalNavigationAsync(browser, target, true)));
            tests.Add(($"readiness-{target}", browser => ReadinessAsync(browser, target)));
            tests.Add(($"hidden-{target}", browser => HiddenAsync(browser, target)));
            tests.Add(($"detached-{target}", browser => DetachedAsync(browser, target)));
            tests.Add(($"cancel-before-{target}", browser => CancelAsync(browser, target, "before")));
            tests.Add(($"cancel-approval-{target}", browser => CancelAsync(browser, target, "approval")));
            tests.Add(($"cancel-readiness-{target}", browser => CancelAsync(browser, target, "readiness")));
            tests.Add(($"cancel-postapproval-{target}", browser => CancelAfterApprovalAsync(browser, target)));
            tests.Add(($"frame-origin-{target}", browser => FrameOriginAsync(browser, target)));
            tests.Add(($"frame-approval-navigation-{target}", browser => FrameApprovalNavigationAsync(browser, target)));
        }
        foreach (var mode in new[] { "same-site", "mapped", "first-approval", "initial-form-change", "late-mapping-change", "denied" })
        {
            var scenario = mode;
            tests.Add(($"password-destination-{scenario}", browser => PasswordDestinationBoundaryAsync(browser, scenario)));
        }
        tests.Add(("pending-origin-consent-stop-fresh-retry", PendingConsentStopAndFreshRetryAsync));
        tests.Add(("approved-sso", ApprovedSsoAsync));
        tests.Add(("aborted-workflow", AbortedWorkflowAsync));
        tests.Add(("post-fill-healthy-vue-shaped", browser => PostFillBoundaryAsync(browser, "healthy")));
        tests.Add(("post-fill-form-reassociation", browser => PostFillBoundaryAsync(browser, "form-reassociation")));
        tests.Add(("post-fill-policy-revocation", browser => PostFillBoundaryAsync(browser, "policy")));
        tests.Add(("post-fill-mapping-invalidation", browser => PostFillBoundaryAsync(browser, "mapping")));
        tests.Add(("post-fill-cancellation", browser => PostFillBoundaryAsync(browser, "cancel")));
        var filter = args.FirstOrDefault(a => a.StartsWith("--case=", StringComparison.Ordinal))?[7..];
        var selected = tests.Where(t => filter is null || t.Name == filter).ToArray();
        if (selected.Length == 0) throw new ArgumentException("Unknown document-bound test case");
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var failures = new List<string>();
        foreach (var test in selected)
        {
            try { await test.Run(browser); Console.WriteLine("PASS CASE " + test.Name); }
            catch (Exception ex)
            {
                failures.Add(test.Name);
                // Assertions and fixture diagnostics never contain credential values or raw Playwright call logs.
                Console.WriteLine("FAIL CASE " + test.Name + " (" + ex.GetType().Name + ")");
                if (ex is TestFailure) Console.WriteLine(ex.Message);
            }
        }
        Console.WriteLine($"Document-bound credential cases: {selected.Length - failures.Count}/{selected.Length}; assertions passed: {_checks}.");
        if (failures.Count != 0) throw new TestFailure("Failed cases: " + string.Join(", ", failures));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public IBrowserContext Context { get; }
        public IPage Page { get; }
        public CredentialVault Vault { get; } = new();
        public AutomationEngine Engine { get; }
        public WebsiteWorkflow Workflow { get; }
        public List<string> Prompts { get; } = new();
        public List<string> Logs { get; } = new();
        public System.Collections.Concurrent.ConcurrentQueue<string> Insertions { get; } = new();
        private const string TrackInputs = """
            document.addEventListener('input', event => {
                if (event.target.id === 'user' || event.target.id === 'password')
                    void window.__credentialFixtureInput(event.target.id);
            }, true);
            """;
        private Fixture(IBrowserContext context, IPage page, string field)
        {
            Context = context;
            Page = page;
            Engine = new(Vault);
            Engine.OnLog += message => Logs.Add(message.Replace(Vault.Credential.Username, "[redacted]").Replace(Vault.Credential.Password, "[redacted]"));
            Engine.ApproveLoginOriginAsync = (_, origin) => { Prompts.Add(origin); return Task.FromResult(false); };
            Workflow = new()
            {
                StartUrl = "https://trusted.test/", UsernameSelector = field == "user" ? "#user" : "",
                PasswordSelector = field == "password" ? "#password" : "", SubmitSelector = "#submit",
                LoginSuccessSelector = "body", LoginWaitSeconds = 5
            };
        }
        public static async Task<Fixture> CreateAsync(IBrowser browser, string field, string initial,
            string url = "https://trusted.test/", string replacement = "")
        {
            var context = await browser.NewContextAsync();
            context.SetDefaultTimeout(2500);
            await context.RouteAsync("**/*", route => route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = route.Request.Url == url ? initial : replacement == "" ? Fields : replacement
            }));
            var page = await context.NewPageAsync();
            var fixture = new Fixture(context, page, field);
            if (field == "password")
                fixture.Workflow.PasswordCredentialOrigin = new Uri(url).GetLeftPart(UriPartial.Authority);
            await context.ExposeFunctionAsync("__credentialFixtureInput", (string id) => fixture.Insertions.Enqueue(id));
            await context.AddInitScriptAsync(TrackInputs);
            await page.GotoAsync(url);
            return fixture;
        }
        public async Task ReplaceContentAsync(string html)
        {
            // document.open removes listeners without rerunning init scripts; keep transient-fill detection on replacements.
            await Page.SetContentAsync(html);
            await Page.EvaluateAsync("() => { " + TrackInputs + " }");
        }
        public Task LoginAsync(CancellationToken ct = default) =>
            (Task)typeof(AutomationEngine).GetMethod("TryAutoLoginAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Engine, new object?[] { Workflow, Page, ct, null, null })!;
        public async Task EmptyAsync(string field, string description) =>
            Check(await Page.InputValueAsync("#" + field) == "" && !Insertions.Contains(field), description);
        public void ManualRetry() => Check(Logs.Any(l => l.Contains("manual", StringComparison.OrdinalIgnoreCase)), "aborted attempt reports manual sign-in/retry");
        public async ValueTask DisposeAsync() { await Engine.DisposeAsync(); await Context.DisposeAsync(); }
    }

    private const string Fields = "<input id='user'><input id='password' type='password'><button id='submit' onclick=\"document.body.dataset.submitted='yes'\">submit</button>";
    private static string Input(string field, string attributes = "") => $"<input id='{field}' type='{(field == "password" ? "password" : "text")}' {attributes}>";

    private static async Task SpaRenderedLoginAsync(IBrowser browser)
    {
        await using var f = await Fixture.CreateAsync(browser, "user", """
            <script>
            setTimeout(() => {
              const form = document.createElement('form');
              form.innerHTML = '<input id="user"><input id="password" type="password"><button id="submit" type="submit">Sign in</button>';
              form.addEventListener('submit', event => {
                event.preventDefault();
                document.body.dataset.submitted = 'yes';
              });
              document.body.append(form);
            }, 300);
            </script>
            """);
        f.Workflow.PasswordSelector = "#password";
        await f.LoginAsync();
        Check(await f.Page.InputValueAsync("#user") == f.Vault.Credential.Username &&
              await f.Page.InputValueAsync("#password") == f.Vault.Credential.Password,
            "late JavaScript-rendered login fields receive the configured credential");
        Check(await f.Page.GetAttributeAsync("body", "data-submitted") == "yes",
            "late rendered login form submits once the username and password are filled");
    }

    private static async Task HiddenFirstLoginAsync(IBrowser browser)
    {
        await using var f = await Fixture.CreateAsync(browser, "user", """
            <form onsubmit="event.preventDefault();document.body.dataset.submitted='yes'">
              <input id="hiddenUser" type="text" style="display:none">
              <input id="user" type="text">
              <input id="hiddenPassword" type="password" style="display:none">
              <input id="password" type="password">
              <button type="submit" hidden>Do not use</button>
              <button id="submit" type="submit">Sign in</button>
            </form>
            """);
        f.Workflow.UsernameSelector = "input[type=text]";
        f.Workflow.PasswordSelector = "input[type=password]";
        f.Workflow.SubmitSelector = "button[type=submit]";
        await f.LoginAsync();
        Check(await f.Page.InputValueAsync("#user") == f.Vault.Credential.Username &&
              await f.Page.InputValueAsync("#password") == f.Vault.Credential.Password &&
              await f.Page.InputValueAsync("#hiddenUser") == "" &&
              await f.Page.InputValueAsync("#hiddenPassword") == "",
            "login chooses visible fields instead of hidden duplicate template inputs");
        Check(await f.Page.GetAttributeAsync("body", "data-submitted") == "yes",
            "login clicks the visible submit button even if a hidden match comes first");
    }

    private static async Task MissingRequiredPasswordAsync(IBrowser browser)
    {
        await using var f = await Fixture.CreateAsync(browser, "user", """
            <form onsubmit="event.preventDefault();document.body.dataset.submitted='yes'">
              <input id="user"><button id="submit" type="submit">Sign in</button>
            </form>
            """);
        f.Workflow.PasswordSelector = "#password";
        f.Workflow.LoginWaitSeconds = 1;
        await f.LoginAsync();
        Check(await f.Page.InputValueAsync("#user") == f.Vault.Credential.Username,
            "available username can still be filled");
        Check(await f.Page.GetAttributeAsync("body", "data-submitted") is null,
            "incomplete login does not click Submit when configured password field is absent");
        f.ManualRetry();
    }

    private static async Task TwoStageJavaScriptPasswordAsync(IBrowser browser)
    {
        await using var f = await Fixture.CreateAsync(browser, "user", """
            <input id="user"><button id="next" onclick="
              setTimeout(() => {
                document.body.innerHTML = '<input id=password type=password><button id=submit onclick=&quot;document.body.dataset.submitted=\'yes\'&quot;>Sign in</button>';
              }, 350);
            ">Next</button>
            """);
        f.Workflow.UsernameContinueSelector = "#next";
        f.Workflow.PasswordSelector = "#password";
        await f.LoginAsync();
        Check(await f.Page.InputValueAsync("#password") == f.Vault.Credential.Password,
            "two-stage JS login fills its delayed password field after Next");
        Check(await f.Page.GetAttributeAsync("body", "data-submitted") == "yes",
            "two-stage JS login submits after delayed password entry");
    }

    private static async Task ExistingSignedInSessionAsync(IBrowser browser)
    {
        await using var f = await Fixture.CreateAsync(browser, "user",
            "<main id='signed-in'><h1>Dashboard</h1></main>");
        f.Workflow.PasswordSelector = "#password";
        f.Workflow.LoginSuccessSelector = "#signed-in";
        await f.LoginAsync();
        Check(f.Logs.Any(x => x.Contains("Already signed in", StringComparison.Ordinal)),
            "saved signed-in session does not depend on a missing login form");
        Check(f.Prompts.Count == 0, "already authenticated website does not request credential origin approval");
    }

    private static async Task DelayedPasswordAsync(IBrowser browser)
    {
        // Preserve the independent reviewer's 1200ms navigation versus the engine's 800ms username-continue wait.
        await using var f = await Fixture.CreateAsync(browser, "user",
            "<input id='user'><input id='password' type='password' disabled><button id='next' onclick=\"setTimeout(()=>location.href='https://unapproved.test/',1200)\">next</button>");
        f.Workflow.UsernameContinueSelector = "#next";
        f.Workflow.PasswordSelector = "#password";
        f.Workflow.SubmitSelector = "";
        // Observation includes the preceding username fill/click as well as the unchanged 1200ms redirect timer.
        var navigation = f.Page.WaitForURLAsync("https://unapproved.test/", new() { WaitUntil = WaitUntilState.Commit, Timeout = 10_000 });
        await f.LoginAsync();
        try { await navigation; }
        catch (TimeoutException) { throw new TestFailure("delayed fixture did not navigate (URL " + f.Page.Url + "): " + string.Join("; ", f.Logs)); }
        Check(f.Page.Url.StartsWith("https://unapproved.test/", StringComparison.Ordinal), "delayed password fixture navigated while the old field was disabled");
        await f.EmptyAsync("password", "delayed password navigation never receives credentials from an earlier document approval");
        Check(f.Prompts.Count == 0, "delayed password navigation aborts instead of approving a replacement document");
        f.ManualRetry();
    }

    private static async Task WaitingNavigationAsync(IBrowser browser, string field)
    {
        await using var f = await Fixture.CreateAsync(browser, field, Input(field, "disabled"));
        var login = f.LoginAsync();
        await Task.Delay(150);
        Check(!login.IsCompleted, "disabled " + field + " keeps the insertion pending");
        await f.Page.GotoAsync("https://unapproved.test/");
        await login;
        await f.EmptyAsync(field, "waiting " + field + " never retargets after navigation");
        Check(f.Prompts.Count == 0, "navigation while waiting does not request approval for a replacement field");
        f.ManualRetry();
    }

    private static async Task ApprovalNavigationAsync(IBrowser browser, string field, bool replaceDocument)
    {
        await using var f = await Fixture.CreateAsync(browser, field, Fields, "https://approval.test/");
        f.Engine.ApproveLoginOriginAsync = async (_, origin) =>
        {
            f.Prompts.Add(origin);
            if (replaceDocument) await f.ReplaceContentAsync(Fields);
            else await f.Page.GotoAsync("https://trusted.test/replacement");
            return true;
        };
        await f.LoginAsync();
        await f.EmptyAsync(field, "approval cannot transfer to a " + (replaceDocument ? "same-origin replacement element" : "new document"));
        Check(f.Prompts.SequenceEqual(new[] { "https://approval.test" }), "approval is requested for the field's original document origin");
        Check(await f.Page.GetAttributeAsync("body", "data-submitted") is null, "aborted approval does not submit the replacement document");
        f.ManualRetry();
    }

    private static async Task ReadinessAsync(IBrowser browser, string field)
    {
        await using var f = await Fixture.CreateAsync(browser, field, Input(field, "disabled readonly style='display:none'"), "https://approval.test/");
        f.Workflow.SubmitSelector = "";
        f.Engine.ApproveLoginOriginAsync = (_, origin) => { f.Prompts.Add(origin); return Task.FromResult(true); };
        var login = f.LoginAsync();
        await Task.Delay(150);
        Check(!login.IsCompleted, "hidden/disabled " + field + " waits for readiness");
        await f.EmptyAsync(field, "not-ready bound " + field + " remains empty");
        Check(f.Prompts.Count == 0, "approval is deferred until the bound element is ready");
        await f.Page.EvaluateAsync("field => { const e = document.getElementById(field); e.disabled = false; e.style.display = ''; }", field);
        await Task.Delay(150);
        Check(!login.IsCompleted && f.Prompts.Count == 0, "visible enabled but read-only " + field + " is not ready for approval/fill");
        await f.EmptyAsync(field, "read-only bound " + field + " remains empty");
        await f.Page.EvaluateAsync("field => document.getElementById(field).readOnly = false", field);
        await login;
        var expected = field == "user" ? f.Vault.Credential.Username : f.Vault.Credential.Password;
        Check(await f.Page.InputValueAsync("#" + field) == expected, "same bound " + field + " fills after becoming ready");
        Check(f.Prompts.SequenceEqual(new[] { "https://approval.test" }), "ready bound " + field + " receives approval for its actual origin");
    }

    private static async Task HiddenAsync(IBrowser browser, string field)
    {
        await using var f = await Fixture.CreateAsync(browser, field, Input(field, "style='display:none'"), "https://approval.test/");
        f.Page.SetDefaultTimeout(350);
        await f.LoginAsync();
        await f.EmptyAsync(field, "permanently hidden " + field + " is not filled");
        Check(f.Prompts.Count == 0, "hidden field timeout does not prompt for unusable credentials");
        f.ManualRetry();
    }

    private static async Task DetachedAsync(IBrowser browser, string field)
    {
        await using var f = await Fixture.CreateAsync(browser, field, Input(field, "disabled"));
        var login = f.LoginAsync();
        await Task.Delay(150);
        Check(!login.IsCompleted, "detachment fixture is waiting on the disabled " + field);
        await f.Page.EvaluateAsync("field => { const e = document.getElementById(field); const replacement = e.cloneNode(); replacement.disabled = false; e.replaceWith(replacement); }", field);
        await login;
        await f.EmptyAsync(field, "detached " + field + " is not replaced by a new matching element");
        f.ManualRetry();
    }

    private static async Task CancelAsync(IBrowser browser, string field, string phase)
    {
        await using var f = await Fixture.CreateAsync(browser, field, Input(field, phase == "readiness" ? "disabled" : ""), "https://approval.test/");
        using var cts = new CancellationTokenSource();
        var approvalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvalReleased = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Engine.ApproveLoginOriginAsync = (_, origin) =>
        {
            f.Prompts.Add(origin);
            if (phase == "approval")
            {
                approvalEntered.SetResult();
                return approvalReleased.Task;
            }
            return Task.FromResult(true);
        };
        if (phase == "before") cts.Cancel();
        var login = f.LoginAsync(cts.Token);
        if (phase == "approval")
        {
            await approvalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel();
        }
        if (phase == "readiness") { await Task.Delay(150); cts.Cancel(); }
        var cancelled = false;
        try { await login.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { cancelled = true; }
        finally { approvalReleased.TrySetResult(true); }
        Check(cancelled, "cancellation during " + phase + " propagates before " + field + " insertion");
        await f.Page.EvaluateAsync("field => document.getElementById(field).disabled = false", field);
        await Task.Delay(100);
        await f.EmptyAsync(field, "cancelled " + field + " has no deferred fill after readiness changes");
        if (phase != "approval") Check(f.Prompts.Count == 0, "cancelled attempt never starts origin approval");
    }

    private static async Task CancelAfterApprovalAsync(IBrowser browser, string field)
    {
        await using var f = await Fixture.CreateAsync(browser, field, Input(field), "https://approval.test/");
        using var cts = new CancellationTokenSource();
        var approved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Engine.ApproveLoginOriginAsync = async (_, origin) =>
        {
            f.Prompts.Add(origin);
            await f.Page.EvaluateAsync("field => document.getElementById(field).disabled = true", field);
            approved.SetResult();
            return true;
        };
        var login = f.LoginAsync(cts.Token);
        await approved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(150);
        Check(!login.IsCompleted, "approval-time disabled " + field + " requires readiness again");
        cts.Cancel();
        var cancelled = false;
        try { await login.WaitAsync(TimeSpan.FromSeconds(4)); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "cancellation during post-approval readiness propagates before " + field + " insertion");
        await f.Page.EvaluateAsync("field => document.getElementById(field).disabled = false", field);
        await Task.Delay(100);
        await f.EmptyAsync(field, "post-approval cancellation does not leave an auto-waiting fill behind");
    }

    private static async Task FrameApprovalNavigationAsync(IBrowser browser, string field)
    {
        await using var f = await Fixture.CreateAsync(browser, field, "<iframe src='https://frame.test/'></iframe>");
        await f.Page.FrameLocator("iframe").Locator("#" + field).WaitForAsync();
        f.Workflow.UsernameSelector = field == "user" ? "iframe >> internal:control=enter-frame >> #user" : "";
        f.Workflow.PasswordSelector = field == "password" ? "iframe >> internal:control=enter-frame >> #password" : "";
        f.Workflow.SubmitSelector = "";
        if (field == "password") f.Workflow.PasswordCredentialOrigin = "https://frame.test";
        f.Engine.ApproveLoginOriginAsync = async (_, origin) =>
        {
            f.Prompts.Add(origin);
            await f.Page.Frames.Single(frame => frame.Url == "https://frame.test/").GotoAsync("https://trusted.test/frame-replacement");
            return true;
        };
        await f.LoginAsync();
        Check(f.Prompts.SequenceEqual(new[] { "https://frame.test" }), "child-frame approval is tied to the original owner frame origin");
        Check(await f.Page.FrameLocator("iframe").Locator("#" + field).InputValueAsync() == "", "navigation of the owning child frame during approval aborts " + field + " insertion");
        Check(f.Page.Url == "https://trusted.test/", "child-frame navigation fixture leaves the top-level URL unchanged");
        f.ManualRetry();
    }

    private static async Task FrameOriginAsync(IBrowser browser, string field)
    {
        await using var f = await Fixture.CreateAsync(browser, field, "<iframe src='https://frame.test/'></iframe>");
        await f.Page.FrameLocator("iframe").Locator("#" + field).WaitForAsync();
        f.Workflow.UsernameSelector = field == "user" ? "iframe >> internal:control=enter-frame >> #user" : "";
        f.Workflow.PasswordSelector = field == "password" ? "iframe >> internal:control=enter-frame >> #password" : "";
        f.Workflow.SubmitSelector = "";
        if (field == "password") f.Workflow.PasswordCredentialOrigin = "https://frame.test";
        await f.LoginAsync();
        Check(f.Prompts.SequenceEqual(new[] { "https://frame.test" }), "frame " + field + " approval uses its owner document, not the top-level URL");
        Check(await f.Page.FrameLocator("iframe").Locator("#" + field).InputValueAsync() == "", "denied frame " + field + " stays empty");
        f.Prompts.Clear();
        f.Engine.ApproveLoginOriginAsync = (_, origin) => { f.Prompts.Add(origin); return Task.FromResult(origin == "https://frame.test"); };
        await f.LoginAsync();
        var expected = field == "user" ? f.Vault.Credential.Username : f.Vault.Credential.Password;
        Check(await f.Page.FrameLocator("iframe").Locator("#" + field).InputValueAsync() == expected, "explicitly approved owning frame " + field + " can fill");
    }

    private static async Task ApprovedSsoAsync(IBrowser browser)
    {
        await using var f = await Fixture.CreateAsync(browser, "user", "<input id='user'><button id='next' onclick=\"location.href='https://sso.test/'\">next</button>");
        f.Workflow.UsernameContinueSelector = "#next";
        f.Workflow.PasswordSelector = "#password";
        f.Workflow.PasswordCredentialOrigin = "https://sso.test";
        f.Engine.ApproveLoginOriginAsync = (_, origin) => { f.Prompts.Add(origin); return Task.FromResult(origin == "https://sso.test"); };
        await f.LoginAsync();
        Check(f.Prompts.SequenceEqual(new[] { "https://sso.test" }), "positive SSO approves the new password document separately");
        Check(await f.Page.InputValueAsync("#password") == f.Vault.Credential.Password, "approved SSO password fills its bound document");
        Check(await f.Page.GetAttributeAsync("body", "data-submitted") == "yes", "approved SSO can submit normally");
    }

    // Actual protected production fill and browser-computed HTML input.form
    // ownership; detects transient insertion via the input event, not just value.
    private static async Task PasswordDestinationBoundaryAsync(IBrowser browser, string scenario)
    {
        const string destination = "https://sso-boundary.test";
        const string synthetic = "synthetic-destination-boundary-value";
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(3500);
        await context.RouteAsync(destination + "/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = """
                <form id='login'></form><form id='other'></form>
                <input id='password' type='password' form='login'
                  oninput="document.body.dataset.inputCount = String(Number(document.body.dataset.inputCount || 0) + 1)">
                """
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync(destination + "/password");
        var workflow = new WebsiteWorkflow
        {
            StartUrl = scenario == "same-site" ? destination + "/password" : "https://trusted.test/start",
            PasswordSelector = "#password"
        };
        if (scenario is "mapped" or "late-mapping-change")
            workflow.PasswordCredentialOrigin = destination;
        if (scenario == "mapped") workflow.ApprovedLoginOrigins.Add(destination);
        await using var engine = new AutomationEngine(new CredentialVault());
        var prompts = 0;
        engine.ApproveLoginOriginAsync = async (_, origin) =>
        {
            prompts++;
            Check(origin == destination, "approval request stays on the exact synthetic destination");
            if (scenario == "denied") return false;
            if (scenario == "late-mapping-change")
            {
                if (prompts == 1) workflow.PasswordCredentialOrigin = "https://changed.test";
                else await page.EvaluateAsync("() => document.querySelector('#password').setAttribute('form', 'other')");
            }
            if (scenario == "initial-form-change")
                await page.EvaluateAsync("() => document.querySelector('#password').setAttribute('form', 'other')");
            return true;
        };
        await using var field = await page.QuerySelectorAsync("#password")
            ?? throw new TestFailure("Fixture password field was missing");
        await using var form = await page.EvaluateHandleAsync("() => document.querySelector('#login')");
        await using var document = await page.EvaluateHandleAsync("() => document");
        var protectedFill = typeof(AutomationEngine).GetMethod("FillCredentialAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task<string?>)protectedFill.Invoke(engine, new object?[] {
            workflow, page, "#password", synthetic, CancellationToken.None,
            null, field, form, true, document
        })!;
        var refused = false;
        try { await task; }
        catch (InvalidOperationException) { refused = true; }
        var inputs = int.Parse(await page.GetAttributeAsync("body", "data-input-count") ?? "0");
        var value = await page.InputValueAsync("#password");
        var shouldFill = scenario is "same-site" or "mapped" or "first-approval";
        if (shouldFill)
        {
            Check(!refused && value == synthetic && inputs > 0,
                "valid exact field and HTML form association permits credential insertion");
            Check(prompts == (scenario == "first-approval" ? 1 : 0),
                "legitimate explicit first approval is not repeated");
        }
        else
        {
            Check(refused && value == "" && inputs == 0,
                "invalidated destination or form is rejected before any input event");
            Check(prompts <= 1,
                "final password checkpoint cannot reopen an interactive dialog");
        }
    }

    private static async Task PendingConsentStopAndFreshRetryAsync(IBrowser browser)
    {
        await using var f = await Fixture.CreateAsync(browser, "password",
            "<form id='login'><input id='password' type='password' autocomplete='current-password'></form>",
            url: "https://pending-consent.test/");
        f.Workflow.StartUrl = "https://trusted.test/";
        f.Workflow.PasswordCredentialOrigin = "";
        f.Workflow.ApprovedLoginOrigins.Clear();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        f.Engine.ApproveLoginOriginAsync = async (_, origin) =>
        {
            Check(origin == "https://pending-consent.test", "pending consent binds exact origin");
            entered.TrySetResult();
            await release.Task;
            return !cancellation.IsCancellationRequested;
        };
        var stopped = f.LoginAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        release.TrySetResult();
        var aborted = false;
        try { await stopped; }
        catch (OperationCanceledException) { aborted = true; }
        Check(aborted, "Stop during pending consent aborts protected fill");
        await f.EmptyAsync("password", "cancellation prevented input events");
        Check(string.IsNullOrEmpty(f.Workflow.PasswordCredentialOrigin),
            "revoked consent cannot create password mapping");

        f.Engine.ApproveLoginOriginAsync = (_, origin) =>
            Task.FromResult(origin == "https://pending-consent.test");
        await f.LoginAsync();
        Check(await f.Page.InputValueAsync("#password") == f.Vault.Credential.Password &&
            f.Insertions.Contains("password"),
            "fresh authorized retry still fills its own bound field");
    }

    // Post-FillAsync sync is a second credential write. Adversarial first-input
    // handlers invalidate ownership before that sync; observe capture-phase events.
    private static async Task PostFillBoundaryAsync(IBrowser browser, string scenario)
    {
        const string destination = "https://post-fill.test";
        const string synthetic = "synthetic-post-fill-boundary-value";
        await using var context = await browser.NewContextAsync();
        context.SetDefaultTimeout(4000);
        await context.RouteAsync(destination + "/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = """
                <form id='login'></form><form id='other'></form>
                <input id='password' type='password' form='login' autocomplete='current-password'>
                <script>
                  window.__events = [];
                  document.addEventListener('input', event => {
                    if (event.target.id !== 'password') return;
                    window.__events.push({
                      formId: event.target.form && event.target.form.id,
                      nonempty: !!event.target.value,
                      inputType: event.inputType || '',
                      hasData: typeof event.data === 'string' && event.data.length > 0,
                      valueLength: (event.target.value || '').length
                    });
                  }, true);
                </script>
                """
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync(destination + "/password");
        var values = new Dictionary<string, object?>();
        using var policy = EnterprisePolicy.UseProviderForTesting(() => values);
        using var cancellation = new CancellationTokenSource();
        var workflow = new WebsiteWorkflow
        {
            // Foreign start URL so password mapping (not same-site) is what authorizes fill.
            StartUrl = "https://trusted.test/start",
            PasswordSelector = "#password",
            PasswordCredentialOrigin = destination
        };
        workflow.ApprovedLoginOrigins.Add(destination);
        await page.ExposeFunctionAsync("__lfInvalidate", () =>
        {
            if (scenario == "policy") values["DisableCredentialFill"] = 1;
            if (scenario == "mapping") workflow.PasswordCredentialOrigin = "https://changed.test";
            if (scenario == "cancel") cancellation.Cancel();
            return scenario;
        });
        if (scenario != "healthy")
        {
            await page.EvaluateAsync("""
                (mode) => {
                  const field = document.querySelector('#password');
                  field.addEventListener('input', () => {
                    if (window.__mutated) return;
                    window.__mutated = true;
                    document.body.dataset.mutated = '1';
                    if (mode === 'form-reassociation') {
                      field.value = '';
                      field.setAttribute('form', 'other');
                    }
                    void window.__lfInvalidate();
                  }, true);
                }
                """, scenario);
        }
        await using var engine = new AutomationEngine(new CredentialVault());
        engine.ApproveLoginOriginAsync = (_, _) => Task.FromResult(true);
        await using var field = await page.QuerySelectorAsync("#password")
            ?? throw new TestFailure("Fixture password field was missing");
        await using var form = await page.EvaluateHandleAsync("() => document.querySelector('#login')");
        await using var document = await page.EvaluateHandleAsync("() => document");
        var protectedFill = typeof(AutomationEngine).GetMethod("FillCredentialAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task<string?>)protectedFill.Invoke(engine, new object?[] {
            workflow, page, "#password", synthetic, cancellation.Token,
            null, field, form, true, document
        })!;
        var refused = false;
        try { await task; }
        catch (InvalidOperationException) { refused = true; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { refused = true; }
        var events = await page.EvaluateAsync<System.Text.Json.JsonElement>("() => window.__events");
        var eventCount = events.GetArrayLength();
        var mutated = await page.GetAttributeAsync("body", "data-mutated") == "1";
        var otherFormInputs = 0;
        var syncWithSecretData = 0;
        for (var i = 0; i < eventCount; i++)
        {
            var ev = events[i];
            if (ev.GetProperty("formId").GetString() == "other" && ev.GetProperty("nonempty").GetBoolean())
                otherFormInputs++;
            // Compatibility sync uses insertFromPaste; it must not also carry secret data.
            if (ev.GetProperty("inputType").GetString() == "insertFromPaste" &&
                ev.GetProperty("hasData").GetBoolean())
                syncWithSecretData++;
        }
        var value = await page.InputValueAsync("#password");
        if (scenario == "healthy")
        {
            Check(!refused && value == synthetic && eventCount >= 1,
                "healthy Vue-shaped post-fill keeps the authorized value and emits model input");
            Check(syncWithSecretData == 0,
                "post-fill insertFromPaste sync does not put the secret in InputEvent.data");
        }
        else
        {
            Check(mutated, scenario + ": adversarial first-input mutation actually ran");
            Check(refused, scenario + ": post-fill sync refuses after ownership invalidation");
            Check(otherFormInputs == 0,
                scenario + ": no nonempty credential input lands on the reassociated form");
            Check(syncWithSecretData == 0,
                scenario + ": refused path emits no secret-bearing insertFromPaste sync");
        }
    }

    private static async Task AbortedWorkflowAsync(IBrowser browser)
    {
        await using var f = await Fixture.CreateAsync(browser, "user", Input("user", "disabled"));
        f.Workflow.Steps.Add(new() { Type = "Click", Target = "#followup" });
        await f.Page.EvaluateAsync("""
            () => {
                const input = document.querySelector('#user');
                const owner = input.ownerDocument;
                Object.defineProperty(input, 'ownerDocument', {
                    configurable: true,
                    get() { owner.body.dataset.credentialBound = 'yes'; return owner; }
                });
            }
            """);
        var sessions = (System.Collections.IDictionary)typeof(AutomationEngine)
            .GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Engine)!;
        var sessionType = typeof(AutomationEngine).GetNestedType("TrackedSession", BindingFlags.NonPublic)!;
        // Inert startup seam: binding must not be inferred from elapsed time while window setup is still pending.
        async Task<ICDPSession> SlowPlacementAsync()
        {
            await Task.Delay(500);
            return await f.Context.NewCDPSessionAsync(f.Page);
        }
        var context = ForwardProxy<IBrowserContext>.Create(f.Context, (method, _) =>
            method.Name == nameof(IBrowserContext.NewCDPSessionAsync) ? (true, SlowPlacementAsync()) : (false, null));
        var trackedPage = ForwardProxy<IPage>.Create(f.Page, (method, _) => method.Name == "get_Context" ? (true, context) : (false, null));
        sessions.Add(f.Workflow.Id, Activator.CreateInstance(sessionType, f.Context, trackedPage));
        var run = (Task<AutomationEngine.LoginAttention?>)typeof(AutomationEngine)
            .GetMethod("RunOneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(f.Engine, new object?[] { f.Workflow, CancellationToken.None, null, null })!;
        await f.Page.WaitForFunctionAsync("document.body.dataset.credentialBound === 'yes'", null, new() { Timeout = 10_000 });
        Check(!run.IsCompleted, "workflow fixture observed its disabled username during bound-element resolution");
        await f.ReplaceContentAsync("<input id='user'><button id='followup' onclick=\"document.body.dataset.followed='yes'\">continue</button>");
        var attention = await run;
        Check(attention?.WorkflowId == f.Workflow.Id, "aborted credential insertion returns manual attention for the original workflow");
        Check(await f.Page.GetAttributeAsync("body", "data-followed") is null, "aborted credential insertion stops later workflow actions even without a visible password challenge");
        await f.EmptyAsync("user", "aborted workflow never fills a replacement username");
    }

    private sealed class TestFailure(string message) : Exception(message);
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new TestFailure("FAIL " + name);
        _checks++;
        Console.WriteLine("PASS " + name);
    }
}
