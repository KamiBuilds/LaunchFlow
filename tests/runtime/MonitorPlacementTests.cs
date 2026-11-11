using System.Reflection;
using System.Text.Json;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;
using Screen = System.Windows.Forms.Screen;

internal static class MonitorPlacementTests
{
    public static async Task RunAsync(IPlaywright playwright)
    {
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var nativeCdp = await context.NewCDPSessionAsync(page);
        Dictionary<string, object>? positioned = null;
        var maximizeAfterPlacement = 0;
        var cdp = ForwardProxy<ICDPSession>.Create(nativeCdp, (method, args) =>
        {
            if (method.Name != nameof(ICDPSession.SendAsync)) return (false, null);
            if ((string)args![0]! == "Browser.getWindowForTarget")
                return (true, Task.FromResult<JsonElement?>(JsonSerializer.Deserialize<JsonElement>("{\"windowId\":1}")));
            var bounds = (Dictionary<string, object>)((Dictionary<string, object>)args[1]!)["bounds"];
            if (bounds.ContainsKey("left")) positioned = new Dictionary<string, object>(bounds);
            else if (bounds.TryGetValue("windowState", out var state) &&
                     Equals(state, "maximized") && positioned is not null)
                maximizeAfterPlacement++;
            return (true, Task.FromResult<JsonElement?>(null));
        });
        var ownedContext = ForwardProxy<IBrowserContext>.Create(context, (method, _) =>
            method.Name == nameof(IBrowserContext.NewCDPSessionAsync) ? (true, Task.FromResult(cdp)) : (false, null));
        var ownedPage = ForwardProxy<IPage>.Create(page, (method, _) => method.Name == "get_Context" ? (true, ownedContext) : (false, null));
        var workflow = new WebsiteWorkflow { MonitorIndex = 0, MonitorDeviceName = "screen-b" };
        await using var engine = new AutomationEngine(new CredentialVault());
        var placement = typeof(AutomationEngine).GetMethod("PositionWindowAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var previous = Screen.AllScreens;
        var failed = false;
        try
        {
            foreach (var scenario in new[] { "saved identity", "live reorder", "disconnected fallback" })
            {
                Screen.AllScreens = scenario switch
                {
                    "saved identity" => new[] { Display("screen-a", 0, true), Display("screen-b", 1600, false) },
                    "live reorder" => new[] { Display("screen-b", 2600, false), Display("screen-a", 0, true) },
                    _ => new[] { Display("screen-c", 0, false), Display("screen-a", 500, true) }
                };
                var expected = scenario == "disconnected fallback" ? Screen.AllScreens[1].Bounds.Left : Screen.AllScreens.Single(s => s.DeviceName == "screen-b").Bounds.Left;
                positioned = null;
                maximizeAfterPlacement = 0;
                object preference = placement.GetParameters()[1].ParameterType == typeof(int) ? workflow.MonitorIndex : workflow;
                await (Task)placement.Invoke(engine, new object[] { ownedPage, preference })!;
                var passed = positioned is not null &&
                    positioned.TryGetValue("left", out var left) && (int)left == expected &&
                    positioned.TryGetValue("width", out var width) && (int)width == 1200 &&
                    positioned.TryGetValue("height", out var height) && (int)height == 800 &&
                    positioned.TryGetValue("windowState", out var state) && Equals(state, "normal") &&
                    maximizeAfterPlacement == 1 && workflow.MonitorDeviceName == "screen-b";
                Console.WriteLine((passed ? "PASS " : "FAIL ") + "production window placement resolves " + scenario + " at dispatch time");
                failed |= !passed;
            }
        }
        finally { Screen.AllScreens = previous; }
        if (failed) throw new Exception("Engine placement did not honor live monitor preference");
    }
    private static Screen Display(string name, int left, bool primary) => new()
    {
        DeviceName = name, Primary = primary, Bounds = new(left, 0, 1200, 800)
    };
}
