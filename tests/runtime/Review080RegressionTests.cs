using System.Reflection;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;

internal static class Review080RegressionTests
{
    public static async Task RunAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        await context.RouteAsync("https://allowed.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html", Body = "<input id='allowed'>"
        }));
        await context.RouteAsync("https://blocked.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = "<input id='denied' onkeydown=\"if(event.key==='Enter') document.body.dataset.pressed='yes'\">"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://allowed.test/");

        // Prior 96-node prefilter and 48-visible cap both hid an explicit
        // password challenge and admitted manual confirmation/follow-up.
        var ordinary = string.Concat(Enumerable.Range(0, 53).Select(i =>
            "<input id='ordinary-" + i + "'>"));
        await page.SetContentAsync("<div id='signed-in'>Dashboard</div>" + ordinary +
            "<input type='password' id='late-password'>");
        Check((await LoginHealth.VerifySuccessAsync(page, "#signed-in", "")).NeedsAttention,
            "49+ visible ordinary inputs cannot conceal a visible password from login confirmation");
        var hidden = string.Concat(Enumerable.Range(0, 110).Select(i =>
            "<input type='text' style='display:none' id='hidden-" + i + "'>"));
        await page.SetContentAsync(hidden + "<div id='host'></div><div id='signed-in'>Dashboard</div>");
        await page.EvaluateAsync("""
            () => { const root = document.getElementById('host').attachShadow({mode:'open'});
                    root.innerHTML = '<input type="password" id="shadow-password">'; }
            """);
        Check(await LoginHealth.HasStructuralSecurityChallengeAsync(page),
            "110 hidden inputs cannot starve an open-shadow password challenge");
        var ordinaryButtons = string.Concat(Enumerable.Range(0, 55)
            .Select(i => "<button>Continue " + i + "</button>"));
        await page.SetContentAsync("<div id='signed-in'>Dashboard</div>" + ordinaryButtons +
            "<button aria-label='Use passkey'>Use passkey</button>");
        Check(await LoginHealth.HasStructuralSecurityChallengeAsync(page),
            "49+ ordinary buttons cannot conceal a visible passkey challenge");
        await page.SetContentAsync("<div id='signed-in'>Dashboard</div>" + hidden);
        Check(!(await LoginHealth.VerifySuccessAsync(page, "#signed-in", "")).NeedsAttention,
            "hidden fields with a valid success element still permit normal confirmation");

        await page.GotoAsync("https://allowed.test/");
        await page.EvaluateAsync("""
            () => {
              const host = document.createElement('div');
              host.id = 'opaque-shadow-host';
              document.body.append(host);
              const root = host.attachShadow({mode:'closed'});
              const frame = document.createElement('iframe');
              frame.src = 'https://blocked.test/denied';
              root.append(frame);
            }
            """);
        using (var until = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (!page.Frames.Any(f => f.Url == "https://blocked.test/denied"))
            {
                until.Token.ThrowIfCancellationRequested();
                await Task.Delay(25, until.Token);
            }
        }
        var deniedFrame = page.Frames.Single(f => f.Url == "https://blocked.test/denied");
        await deniedFrame.Locator("#denied").FocusAsync();
        Check(await page.EvaluateAsync<bool>("() => document.activeElement?.id === 'opaque-shadow-host'"),
            "closed-shadow iframe focus is retargeted to the opaque top-document host");

        using (EnterprisePolicy.UseProviderForTesting(() =>
            new Dictionary<string, object?> { ["AllowedHosts"] = "allowed.test" }))
        {
            var method = typeof(AutomationEngine).GetMethod("ExecuteStepAsync",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new Exception("Production keyboard execution boundary missing");
            var denied = false;
            try
            {
                await (Task)method.Invoke(null,
                    new object[] { page, new WorkflowStep { Type = "Press", Target = "Enter" },
                        CancellationToken.None })!;
            }
            catch (InvalidOperationException) { denied = true; }
            Check(denied && await deniedFrame.GetAttributeAsync("body", "data-pressed") is null,
                "closed-shadow denied iframe cannot receive a keyboard press");
            await page.SetContentAsync(
                "<input id='allowed' onkeydown=\"if(event.key==='Enter') document.body.dataset.pressed='yes'\">");
            await page.Locator("#allowed").FocusAsync();
            await (Task)method.Invoke(null,
                new object[] { page, new WorkflowStep { Type = "Press", Target = "Enter" },
                    CancellationToken.None })!;
            Check(await page.GetAttributeAsync("body", "data-pressed") == "yes",
                "ordinary allowed-focus keyboard press remains functional");
        }
        Console.WriteLine("Review-080 Chromium authorization regressions passed.");
    }

    private static void Check(bool okay, string description)
    {
        if (!okay) throw new Exception("FAIL " + description);
        Console.WriteLine("PASS " + description);
    }
}
