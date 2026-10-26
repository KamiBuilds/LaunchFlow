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
            ("delayed-password", DelayedPasswordAsync)
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
        tests.Add(("approved-sso", ApprovedSsoAsync));
        tests.Add(("aborted-workflow", AbortedWorkflowAsync));
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
        f.Engine.ApproveLoginOriginAsync = (_, origin) => { f.Prompts.Add(origin); return Task.FromResult(origin == "https://sso.test"); };
        await f.LoginAsync();
        Check(f.Prompts.SequenceEqual(new[] { "https://sso.test" }), "positive SSO approves the new password document separately");
        Check(await f.Page.InputValueAsync("#password") == f.Vault.Credential.Password, "approved SSO password fills its bound document");
        Check(await f.Page.GetAttributeAsync("body", "data-submitted") == "yes", "approved SSO can submit normally");
    }

    private static async Task AbortedWorkflowAsync(IBrowser browser)
    {
        await using var f = await Fixture.CreateAsync(browser, "user", Input("user", "disabled"));
        f.Workflow.Steps.Add(new() { Type = "Click", Target = "#followup" });
        var sessions = (System.Collections.IDictionary)typeof(AutomationEngine)
            .GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Engine)!;
        var sessionType = typeof(AutomationEngine).GetNestedType("TrackedSession", BindingFlags.NonPublic)!;
        sessions.Add(f.Workflow.Id, Activator.CreateInstance(sessionType, f.Context, f.Page));
        var run = (Task<AutomationEngine.LoginAttention?>)typeof(AutomationEngine)
            .GetMethod("RunOneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(f.Engine, new object?[] { f.Workflow, CancellationToken.None, null, null })!;
        await Task.Delay(200);
        Check(!run.IsCompleted, "workflow fixture is awaiting a disabled bound username");
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
