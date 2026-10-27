using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;

internal static class EnginePolicyTests
{
    public static async Task RunAsync(IPlaywright playwright, string[] args)
    {
        if (args.Contains("--document-bound-only") || args.Contains("--profile-only") || args.Contains("--dispatch-only")) return;
        if (args.Contains("--policy-redirect-only"))
        {
            await using var redirectBrowser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            await InitialRedirectAsync(redirectBrowser);
            return;
        }
        await AdmissionAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await BlockedConfiguredUrlsAsync(browser);
        await InitialRedirectAsync(browser);
        await CredentialFlagsAsync(browser);
        await HttpsCredentialDocumentAsync(browser);
        await CredentialFramePolicyAsync(browser);
        await ApprovalRevocationAsync(browser);
        await RestrictedStepsAsync(browser);
        await StepRedirectAsync(browser);
        await DestinationRedirectAsync(browser);
        await RecorderAdmissionAsync(browser);
        await LaunchRevocationAsync(playwright, browser);
        await AllowedHttpsFrameAsync(browser);
        await RecordedFrameTargetAsync(browser);
        await FillResolutionRevocationAsync(browser);
        await FillResolutionCancellationAsync(browser);
    }

    private static async Task AdmissionAsync()
    {
        using var policy = EnterprisePolicy.UseProviderForTesting(() => new Dictionary<string, object?> { ["DisableAutomation"] = 1 });
        await using var engine = new AutomationEngine(new CredentialVault());
        var refused = false;
        try { await engine.RunAsync(Array.Empty<WebsiteWorkflow>(), CancellationToken.None); }
        catch (InvalidOperationException) { refused = true; }
        EngineBehaviorTests.Check(refused && !engine.IsRunning,
            "administrator automation denial refuses public run admission without creating an active run");
    }

    private static async Task BlockedConfiguredUrlsAsync(IBrowser browser)
    {
        foreach (var destination in new[] { false, true })
        {
            await using var fixture = await Fixture.CreateAsync(browser);
            using var policy = EnterprisePolicy.UseProviderForTesting(() => new Dictionary<string, object?> { ["AllowedHosts"] = "allowed.test" });
            if (destination) fixture.Workflow.DestinationUrl = "https://blocked.test/private-destination";
            else fixture.Workflow.StartUrl = "https://blocked.test/private-start";
            fixture.Workflow.Steps.Add(new() { Type = "Click", Target = "#followup" });
            var result = await fixture.Engine.RunAsync(new[] { fixture.Workflow }, CancellationToken.None);
            EngineBehaviorTests.Check(result.FailedCount == 1 && fixture.Page.Url == "https://allowed.test/" &&
                await fixture.Page.GetAttributeAsync("body", "data-followed") is null,
                "blocked configured " + (destination ? "destination" : "start") + " is refused before workflow actions or navigation");
        }
    }

    private static async Task InitialRedirectAsync(IBrowser browser)
    {
        await using var fixture = await Fixture.CreateAsync(browser);
        await using var server = new RedirectServer();
        // Chromium can bypass route interception for a followed HTTP redirect. Serve both hops on owned loopback sockets.
        await fixture.Context.UnrouteAsync("**/*");
        await fixture.Page.GotoAsync("about:blank");
        fixture.Workflow.StartUrl = server.StartUrl;
        fixture.Workflow.Steps.Add(new() { Type = "Click", Target = "#followup" });
        using var policy = EnterprisePolicy.UseProviderForTesting(() => new Dictionary<string, object?> { ["AllowedHosts"] = "127.0.0.1" });
        var result = await fixture.Engine.RunAsync(new[] { fixture.Workflow }, CancellationToken.None);
        EngineBehaviorTests.Check(fixture.Page.Url == server.DestinationUrl && result.FailedCount == 1 &&
            await fixture.Page.GetAttributeAsync("body", "data-followed") is null,
            "an actual initial redirect to a blocked host stops follow-up dispatch");
    }

    private static async Task CredentialFlagsAsync(IBrowser browser)
    {
        var failed = false;
        foreach (var flag in new[] { "DisableCredentialFill", "DisableCredentialStorage" })
        {
            await using var fixture = await Fixture.CreateAsync(browser);
            using var policy = EnterprisePolicy.UseProviderForTesting(() => new Dictionary<string, object?> { [flag] = 1 });
            try { await fixture.LoginAsync(); } catch (InvalidOperationException) { }
            var passed = fixture.Vault.ReadCount == 0 && await fixture.Page.InputValueAsync("#user") == "" &&
                await fixture.Page.InputValueAsync("#password") == "";
            Console.WriteLine((passed ? "PASS " : "FAIL ") + flag + " prevents credential retrieval and insertion");
            failed |= !passed;
        }
        if (failed) throw new Exception("Credential retrieval was not refused by administrator policy");
    }

    private static async Task HttpsCredentialDocumentAsync(IBrowser browser)
    {
        await using var fixture = await Fixture.CreateAsync(browser);
        await fixture.Page.GotoAsync("http://allowed.test/");
        fixture.Workflow.StartUrl = fixture.Page.Url;
        using var policy = EnterprisePolicy.UseProviderForTesting(() => new Dictionary<string, object?> { ["RequireHttpsForCredentials"] = 1 });
        await fixture.LoginAsync();
        EngineBehaviorTests.Check(await fixture.Page.InputValueAsync("#user") == "" && await fixture.Page.InputValueAsync("#password") == "",
            "HTTPS credential policy refuses insertion into an HTTP document");
    }

    private static async Task CredentialFramePolicyAsync(IBrowser browser)
    {
        await using var fixture = await Fixture.CreateAsync(browser);
        await fixture.Page.SetContentAsync("<iframe src='https://blocked.test/field'></iframe>");
        await fixture.Page.FrameLocator("iframe").Locator("#password").WaitForAsync();
        fixture.Workflow.UsernameSelector = "";
        fixture.Workflow.PasswordSelector = "iframe >> internal:control=enter-frame >> #password";
        fixture.Engine.ApproveLoginOriginAsync = (_, _) => Task.FromResult(true);
        using var policy = EnterprisePolicy.UseProviderForTesting(() => new Dictionary<string, object?> { ["AllowedHosts"] = "allowed.test" });
        await fixture.LoginAsync();
        EngineBehaviorTests.Check(await fixture.Page.FrameLocator("iframe").Locator("#password").InputValueAsync() == "",
            "user origin approval cannot override the administrator host restriction on a credential's owning frame");
    }

    private static async Task ApprovalRevocationAsync(IBrowser browser)
    {
        await using var fixture = await Fixture.CreateAsync(browser);
        fixture.Workflow.StartUrl = "https://other-origin.test/";
        fixture.Workflow.PasswordSelector = "";
        var values = new Dictionary<string, object?>();
        using var policy = EnterprisePolicy.UseProviderForTesting(() => values);
        fixture.Engine.ApproveLoginOriginAsync = async (_, _) =>
        {
            await Task.Delay(30);
            values["DisableCredentialFill"] = 1;
            return true;
        };
        await fixture.LoginAsync();
        EngineBehaviorTests.Check(values.ContainsKey("DisableCredentialFill") && await fixture.Page.InputValueAsync("#user") == "",
            "administrator credential permission revoked during asynchronous approval prevents insertion");
    }

    private static async Task RestrictedStepsAsync(IBrowser browser)
    {
        var failed = false;
        foreach (var type in new[] { "Fill", "Navigate" })
        {
            await using var fixture = await Fixture.CreateAsync(browser);
            var values = new Dictionary<string, object?> { ["AllowedHosts"] = "allowed.test" };
            if (type == "Fill") values["DisableCredentialFill"] = 1;
            using var policy = EnterprisePolicy.UseProviderForTesting(() => values);
            fixture.Workflow.Steps.Add(new() { Type = type, Target = type == "Fill" ? "#user" : "https://blocked.test/", Value = "synthetic-action-text" });
            var result = await fixture.Engine.RunAsync(new[] { fixture.Workflow }, CancellationToken.None);
            var passed = result.FailedCount == 1 && fixture.Page.Url == "https://allowed.test/" && await fixture.Page.InputValueAsync("#user") == "";
            Console.WriteLine((passed ? "PASS " : "FAIL ") + "administrator policy blocks recorded " + type + " before dispatch");
            failed |= !passed;
        }
        if (failed) throw new Exception("Recorded step policy was not enforced");
    }

    private static async Task StepRedirectAsync(IBrowser browser)
    {
        await using var fixture = await Fixture.CreateAsync(browser);
        await fixture.Page.SetContentAsync("<button id='followup' onclick=\"location.href='https://blocked.test/'\">redirect</button>");
        fixture.Workflow.Steps.Add(new() { Type = "Click", Target = "#followup" });
        fixture.Workflow.DestinationUrl = "https://allowed.test/final";
        var reachedFinal = false;
        fixture.Page.Request += (_, request) => reachedFinal |= request.Url == fixture.Workflow.DestinationUrl;
        using var policy = EnterprisePolicy.UseProviderForTesting(() => new Dictionary<string, object?> { ["AllowedHosts"] = "allowed.test" });
        var result = await fixture.Engine.RunAsync(new[] { fixture.Workflow }, CancellationToken.None);
        EngineBehaviorTests.Check(result.FailedCount == 1 && !reachedFinal && fixture.Page.Url == "https://blocked.test/",
            "a recorded action redirect to a blocked host prevents final destination dispatch");
    }

    private static async Task DestinationRedirectAsync(IBrowser browser)
    {
        await using var fixture = await Fixture.CreateAsync(browser);
        await using var server = new RedirectServer();
        await fixture.Context.UnrouteAsync("**/*");
        await fixture.Page.GotoAsync(new Uri(new Uri(server.StartUrl), "/ready").ToString());
        fixture.Workflow.StartUrl = fixture.Page.Url;
        fixture.Workflow.DestinationUrl = server.StartUrl;
        using var policy = EnterprisePolicy.UseProviderForTesting(() => new Dictionary<string, object?> { ["AllowedHosts"] = "127.0.0.1" });
        var result = await fixture.Engine.RunAsync(new[] { fixture.Workflow }, CancellationToken.None);
        EngineBehaviorTests.Check(result.FailedCount == 1 && result.FinishedCount == 0 && fixture.Page.Url == server.DestinationUrl,
            "a blocked final-destination redirect is not reported as workflow success");
    }

    private static async Task RecorderAdmissionAsync(IBrowser browser)
    {
        var failed = false;
        foreach (var scenario in new[] { "automation", "teach", "configured host", "actual host" })
        {
            await using var fixture = await Fixture.CreateAsync(browser);
            var values = new Dictionary<string, object?> { ["AllowedHosts"] = "allowed.test" };
            if (scenario == "automation") values["DisableAutomation"] = 1;
            if (scenario == "teach") values["DisableTeach"] = 1;
            if (scenario == "configured host") fixture.Workflow.StartUrl = "https://blocked.test/";
            if (scenario == "actual host") await fixture.Page.GotoAsync("https://blocked.test/");
            using var policy = EnterprisePolicy.UseProviderForTesting(() => values);
            var refused = await EngineLifecycleTests.RejectedAsync(() => fixture.Engine.GetPageForRecorderAsync(fixture.Workflow));
            Console.WriteLine((refused ? "PASS " : "FAIL ") + "recorder acquisition enforces " + scenario + " policy");
            failed |= !refused;
        }
        if (failed) throw new Exception("Recorder acquisition did not enforce policy");
    }

    private static async Task LaunchRevocationAsync(IPlaywright playwright, IBrowser browser)
    {
        await using var fixture = await Fixture.CreateAsync(browser);
        await using var engine = new AutomationEngine(new CredentialVault());
        var entered = EngineLifecycleTests.Signal();
        var release = EngineLifecycleTests.Signal();
        var closed = false;
        var pagesReads = 0;
        var returned = ForwardProxy<IBrowserContext>.Create(fixture.Context, (method, _) =>
        {
            if (method.Name == "get_Pages") pagesReads++;
            if (method.Name != nameof(IBrowserContext.CloseAsync)) return (false, null);
            return (true, CloseAsync());
        });
        async Task CloseAsync() { await fixture.Context.CloseAsync(); closed = true; }
        async Task<IBrowserContext> LaunchAsync() { entered.TrySetResult(); await release.Task; return returned; }
        var type = ForwardProxy<IBrowserType>.Create(playwright.Chromium, (method, _) =>
            method.Name == nameof(IBrowserType.LaunchPersistentContextAsync) ? (true, LaunchAsync()) : (false, null));
        var runtime = ForwardProxy<IPlaywright>.Create(playwright, (method, _) => method.Name switch
        {
            "get_Chromium" => (true, type), nameof(IDisposable.Dispose) => (true, null), _ => (false, null)
        });
        typeof(AutomationEngine).GetField("_playwright", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(engine, runtime);
        var previousXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var root = Path.Combine(Path.GetTempPath(), "launchflow-policy-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", root);
        var values = new Dictionary<string, object?>();
        using var policy = EnterprisePolicy.UseProviderForTesting(() => values);
        try
        {
            if (!OperatingSystem.IsLinux() || Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) != root)
                throw new Exception("HARNESS: launch seam requires isolated Linux XDG data");
            var workflow = new WebsiteWorkflow { StartUrl = "https://allowed.test/", Browser = "Chromium", AutoLogin = false };
            var run = engine.RunAsync(new[] { workflow }, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            values["DisableAutomation"] = 1;
            release.TrySetResult();
            var result = await run;
            EngineBehaviorTests.Check(result.FailedCount == 1 && closed && pagesReads == 0 && EngineLifecycleTests.SessionCount(engine) == 0,
                "policy revoked during a persistent-launch return closes the unclaimed context before inspecting or claiming it");
        }
        finally
        {
            release.TrySetResult();
            await engine.CloseSessionsAsync();
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previousXdg);
            Directory.Delete(root, true);
        }
    }

    private static async Task AllowedHttpsFrameAsync(IBrowser browser)
    {
        await using var fixture = await Fixture.CreateAsync(browser);
        await fixture.Page.GotoAsync("http://allowed.test/");
        fixture.Workflow.StartUrl = fixture.Page.Url;
        await fixture.Page.SetContentAsync("<iframe src='https://allowed.test/field'></iframe>");
        await fixture.Page.FrameLocator("iframe").Locator("#password").WaitForAsync();
        fixture.Workflow.UsernameSelector = "";
        fixture.Workflow.PasswordSelector = "iframe >> internal:control=enter-frame >> #password";
        fixture.Engine.ApproveLoginOriginAsync = (_, _) => Task.FromResult(true);
        using var policy = EnterprisePolicy.UseProviderForTesting(() => new Dictionary<string, object?>
        {
            ["AllowedHosts"] = "allowed.test", ["RequireHttpsForCredentials"] = 1
        });
        await fixture.LoginAsync();
        EngineBehaviorTests.Check(await fixture.Page.FrameLocator("iframe").Locator("#password").InputValueAsync() == fixture.Vault.Credential.Password,
            "positive control: HTTPS is checked on the approved owning frame, not its HTTP top-level page");
    }

    private static async Task RecordedFrameTargetAsync(IBrowser browser)
    {
        await using var fixture = await Fixture.CreateAsync(browser);
        await fixture.Page.SetContentAsync("<iframe src='https://blocked.test/field'></iframe>");
        await fixture.Page.FrameLocator("iframe").Locator("#user").WaitForAsync();
        fixture.Workflow.Steps.Add(new() { Type = "Fill", Target = "iframe >> internal:control=enter-frame >> #user", Value = "synthetic-action-text" });
        using var policy = EnterprisePolicy.UseProviderForTesting(() => new Dictionary<string, object?> { ["AllowedHosts"] = "allowed.test" });
        var result = await fixture.Engine.RunAsync(new[] { fixture.Workflow }, CancellationToken.None);
        EngineBehaviorTests.Check(result.FailedCount == 1 && await fixture.Page.FrameLocator("iframe").Locator("#user").InputValueAsync() == "",
            "recorded element actions authorize their actual owning document, not only the top-level host");
    }

    private static async Task FillResolutionRevocationAsync(IBrowser browser)
    {
        await using var fixture = await Fixture.CreateAsync(browser);
        var values = new Dictionary<string, object?>();
        using var policy = EnterprisePolicy.UseProviderForTesting(() => values);
        var actual = fixture.Page.Locator("#user").First;
        async Task<IElementHandle?> ResolveAsync()
        {
            var element = await actual.ElementHandleAsync();
            values["DisableCredentialFill"] = 1;
            return element;
        }
        ILocator controlled = null!;
        controlled = ForwardProxy<ILocator>.Create(actual, (method, _) => method.Name switch
        {
            "get_First" => (true, controlled), nameof(ILocator.ElementHandleAsync) => (true, ResolveAsync()), _ => (false, null)
        });
        var page = ForwardProxy<IPage>.Create(fixture.Page, (method, _) => method.Name == nameof(IPage.Locator) ? (true, controlled) : (false, null));
        await using var engine = new AutomationEngine(new CredentialVault());
        EngineBehaviorTests.Track(engine, fixture.Workflow, fixture.Context, page);
        fixture.Workflow.Steps.Add(new() { Type = "Fill", Target = "#user", Value = "synthetic-action-text" });
        var result = await engine.RunAsync(new[] { fixture.Workflow }, CancellationToken.None);
        EngineBehaviorTests.Check(values.ContainsKey("DisableCredentialFill") && result.FailedCount == 1 && await fixture.Page.InputValueAsync("#user") == "",
            "policy revoked across element resolution is checked again before recorded Fill dispatch");
    }

    private static async Task FillResolutionCancellationAsync(IBrowser browser)
    {
        await using var fixture = await Fixture.CreateAsync(browser);
        using var cancellation = new CancellationTokenSource();
        var actual = fixture.Page.Locator("#user").First;
        async Task<IElementHandle?> ResolveAsync()
        {
            var element = await actual.ElementHandleAsync();
            cancellation.Cancel();
            return element;
        }
        ILocator controlled = null!;
        controlled = ForwardProxy<ILocator>.Create(actual, (method, _) => method.Name switch
        {
            "get_First" => (true, controlled), nameof(ILocator.ElementHandleAsync) => (true, ResolveAsync()), _ => (false, null)
        });
        var page = ForwardProxy<IPage>.Create(fixture.Page, (method, _) => method.Name == nameof(IPage.Locator) ? (true, controlled) : (false, null));
        await using var engine = new AutomationEngine(new CredentialVault());
        EngineBehaviorTests.Track(engine, fixture.Workflow, fixture.Context, page);
        fixture.Workflow.Steps.Add(new() { Type = "Fill", Target = "#user", Value = "synthetic-action-text" });
        var cancelled = await EngineLifecycleTests.CancelledAsync(engine.RunAsync(new[] { fixture.Workflow }, cancellation.Token));
        EngineBehaviorTests.Check(cancelled && await fixture.Page.InputValueAsync("#user") == "",
            "cancellation across element resolution prevents a not-yet-dispatched Fill");
    }

    private sealed class RedirectServer : IAsyncDisposable
    {
        private readonly System.Net.HttpListener _listener = new();
        private readonly Task _serving;
        public string StartUrl { get; }
        public string DestinationUrl { get; }
        public RedirectServer()
        {
            var reservation = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            StartUrl = $"http://127.0.0.1:{port}/start";
            DestinationUrl = $"http://localhost:{port}/destination";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _serving = ServeAsync();
        }
        private async Task ServeAsync()
        {
            try
            {
                while (_listener.IsListening)
                {
                    var request = await _listener.GetContextAsync();
                    var response = request.Response;
                    response.KeepAlive = false;
                    if (request.Request.Url!.AbsolutePath == "/start")
                    {
                        response.StatusCode = 302;
                        response.RedirectLocation = DestinationUrl;
                        response.ContentLength64 = 0;
                    }
                    else
                    {
                        var body = System.Text.Encoding.UTF8.GetBytes("<button id='followup' onclick=\"document.body.dataset.followed='yes'\">follow up</button>");
                        response.ContentType = "text/html";
                        response.ContentLength64 = body.Length;
                        await response.OutputStream.WriteAsync(body);
                    }
                    response.Close();
                }
            }
            catch (Exception ex) when (!_listener.IsListening && ex is System.Net.HttpListenerException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync() { _listener.Close(); await _serving; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public IBrowserContext Context { get; }
        public IPage Page { get; }
        public CredentialVault Vault { get; } = new();
        public AutomationEngine Engine { get; }
        public WebsiteWorkflow Workflow { get; }
        private Fixture(IBrowserContext context, IPage page)
        {
            Context = context; Page = page; Engine = new(Vault);
            Workflow = new() { StartUrl = page.Url, AutoLogin = false, UsernameSelector = "#user", PasswordSelector = "#password", SubmitSelector = "", LoginSuccessSelector = "body" };
            EngineBehaviorTests.Track(Engine, Workflow, context, page);
        }
        public static async Task<Fixture> CreateAsync(IBrowser browser)
        {
            var context = await browser.NewContextAsync();
            context.SetDefaultTimeout(2500);
            context.SetDefaultNavigationTimeout(15_000);
            await context.RouteAsync("**/*", route => route.FulfillAsync(new() { ContentType = "text/html", Body = "<input id='user'><input id='password' type='password'><button id='followup' onclick=\"document.body.dataset.followed='yes'\">follow up</button>" }));
            var page = await context.NewPageAsync();
            await page.GotoAsync("https://allowed.test/");
            return new(context, page);
        }
        public Task LoginAsync() => (Task)typeof(AutomationEngine).GetMethod("TryAutoLoginAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(Engine, new object?[] { Workflow, Page, CancellationToken.None, null, null })!;
        public async ValueTask DisposeAsync() { await Engine.DisposeAsync(); await Context.DisposeAsync(); }
    }
}
