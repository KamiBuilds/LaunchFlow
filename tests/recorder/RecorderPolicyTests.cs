using LaunchFlow;
using LaunchFlow.Services;
using Microsoft.Playwright;

internal static class RecorderPolicyTests
{
    private static IReadOnlyDictionary<string, object?> _values = new Dictionary<string, object?>();
    internal static IDisposable Provider() => EnterprisePolicy.UseProviderForTesting(() => Volatile.Read(ref _values));
    internal static void Policy(params (string Name, object? Value)[] values) =>
        Volatile.Write(ref _values, values.ToDictionary(v => v.Name, v => v.Value));

    internal static void Construction(IPage page)
    {
        foreach (var flag in new[] { "DisableTeach", "DisableAutomation" })
        {
            Policy((flag, 1));
            Refused(() => new TeachRecorder(page), flag + " refuses recorder construction");
            Refused(() => new VisualRecorderWindow(page), flag + " refuses window construction");
        }
        Policy(("AllowedHosts", "other.test"));
        Refused(() => new TeachRecorder(page), "actual page host gates construction");
        Policy();
    }

    internal static async Task DrainPolicy(IPage page)
    {
        foreach (var change in new[] { ("DisableTeach", (object?)1), ("DisableAutomation", (object?)1), ("AllowedHosts", (object?)"blocked.test") })
        {
            Policy();
            using var recorder = new TeachRecorder(page);
            await recorder.SynchronizeAsync();
            await page.ClickAsync("button");
            await Flush(page);
            Policy(change);
            Check(recorder.Drain().Count == 0 && !recorder.IsCapturing,
                change.Item1 + " after capture clears pending output and pauses");
            Policy();
            Check(recorder.Drain().Count == 0, "policy restoration never resurrects denied queued clicks");
        }
    }

    internal static async Task NativeAcceptance(IPage page)
    {
        foreach (var flag in new[] { "DisableTeach", "DisableAutomation" })
        {
            Policy();
            using var recorder = new TeachRecorder(page);
            await recorder.SynchronizeAsync();
            Policy((flag, 1));
            await page.ClickAsync("button");
            await Flush(page);
            Policy();
            Check(recorder.Drain().Count == 0 && !recorder.IsCapturing,
                flag + " is enforced at native callback even if restored before Drain");
        }

        await page.Context.RouteAsync("https://blocked.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html", Body = "<button>Blocked</button>"
        }));
        Policy(("AllowedHosts", "recorder.test"));
        using (var recorder = new TeachRecorder(page))
        {
            await recorder.SynchronizeAsync();
            await page.GotoAsync("https://blocked.test/redirected");
            await Flush(page);
            await page.ClickAsync("button");
            await Flush(page);
            await page.GotoAsync("https://recorder.test/returned");
            await Flush(page);
            Check(recorder.Drain().Count == 0 && !recorder.IsCapturing,
                "actual navigated page URL is checked before native admission, not configured start");
        }
        using (var recorder = new TeachRecorder(page))
        {
            await recorder.SynchronizeAsync();
            await page.EvaluateAsync("() => { const f=document.createElement('iframe'); f.src='https://blocked.test/frame'; document.body.append(f); }");
            await WaitUntil(() => page.Frames.Any(f => f.Url == "https://blocked.test/frame"));
            var frame = page.Frames.Single(f => f.Url == "https://blocked.test/frame");
            await frame.WaitForSelectorAsync("button");
            await frame.EvaluateAsync("""() => window.__launchFlowTeachDispatchV1('{"type":"hello"}')""");
            await frame.ClickAsync("button");
            await frame.EvaluateAsync("""() => window.__launchFlowTeachDispatchV1('{"type":"hello"}')""");
            Check(recorder.Drain().Count == 0 && !recorder.IsCapturing && recorder.UnsupportedTargetObserved,
                "disallowed owning frame pauses capture and preserves unsupported-target status");
        }
        await page.GotoAsync("https://recorder.test/provenance");
        Policy();
        using (var recorder = new TeachRecorder(page))
        {
            await recorder.SynchronizeAsync();
            await page.ClickAsync("button");
            await Flush(page);
            Check(recorder.Drain().Count == 1, "allowed native capture and output remain supported");
            await page.GotoAsync("https://blocked.test/later-allowed");
            Policy(("AllowedHosts", "blocked.test"));
            Check(!recorder.CheckPolicy(), "already drained click provenance is revalidated after host list changes");
        }
        Policy();
        await page.GotoAsync("https://recorder.test/initial");
    }

    internal static async Task WindowPolicy(IPage page)
    {
        foreach (var change in new[] { ("DisableTeach", (object?)1), ("DisableAutomation", (object?)1), ("AllowedHosts", (object?)"blocked.test") })
        foreach (var drained in new[] { false, true })
        {
            Policy();
            var window = await OpenWindow(page);
            await page.ClickAsync("button");
            await Flush(page);
            if (drained) window.TestTick();
            Policy(change);
            window.TestSave();
            Check(!window.Saved && window.RecordedSteps.Count == 0 && DeniedStatus(window),
                change.Item1 + " before Save clears " + (drained ? "displayed" : "queued") + " clicks with fixed status");
            window.TestCancel();
            await window.TestWaitForUpdatesAsync();
        }

        Policy();
        var startup = new VisualRecorderWindow(page);
        Policy(("DisableTeach", 1));
        startup.TestLoad();
        await startup.TestWaitForUpdatesAsync();
        startup.TestTick();
        Check(DeniedStatus(startup) && startup.RecordedSteps.Count == 0, "policy change between construction and startup is visibly refused");
        startup.TestToggle();
        await startup.TestWaitForUpdatesAsync();
        Check(DeniedStatus(startup), "blocked async toggle completes with fixed denial rather than escaping exception");
        startup.TestClear();
        Check(DeniedStatus(startup), "Clear never overwrites policy denial status");
        startup.TestCancel();
        await startup.TestWaitForUpdatesAsync();

        Policy();
        var manual = await OpenWindow(page);
        Policy(("AllowedHosts", "recorder.test"));
        StepEditorWindow.OnShow = _ => true;
        manual.TestEditStep("Navigate", "https://blocked.test/private-url");
        Check(manual.RecordedSteps.Count == 0 && DeniedStatus(manual), "explicit blocked Navigate cannot enter the saved step list");
        manual.TestCancel();
        await manual.TestWaitForUpdatesAsync();

        Policy();
        var modal = await OpenWindow(page);
        StepEditorWindow.OnShow = _ => { Policy(("DisableTeach", 1)); return true; };
        modal.TestEditStep("Wait", "1500");
        Check(modal.RecordedSteps.Count == 0 && DeniedStatus(modal), "manual editing rechecks policy after modal await/reentrancy");
        modal.TestCancel();
        await modal.TestWaitForUpdatesAsync();
        StepEditorWindow.OnShow = _ => true;

        Policy();
        var navigation = await OpenWindow(page);
        navigation.TestEditStep("Navigate", "https://blocked.test/was-allowed");
        Check(navigation.RecordedSteps.Count == 1, "allowed manual Navigate is admitted");
        Policy(("AllowedHosts", "recorder.test"));
        navigation.TestSave();
        Check(!navigation.Saved && navigation.RecordedSteps.Count == 0 && DeniedStatus(navigation), "Save revalidates previously allowed explicit Navigate URLs");
        navigation.TestCancel();
        await navigation.TestWaitForUpdatesAsync();

        Policy();
        var urls = await OpenWindow(page);
        var popup = await page.Context.NewPageAsync();
        await popup.GotoAsync("https://blocked.test/capture-url");
        Policy(("AllowedHosts", "recorder.test"));
        urls.TestCaptureUrl();
        Check(urls.RecordedSteps.Count == 0 && DeniedStatus(urls), "latest-tab URL capture refuses disallowed actual destination");
        urls.TestCancel();
        await urls.TestWaitForUpdatesAsync();
        await popup.CloseAsync();

        Policy();
        var provenance = await OpenWindow(page);
        await page.ClickAsync("button");
        await Flush(page);
        provenance.TestTick();
        await page.GotoAsync("https://blocked.test/now-allowed");
        Policy(("AllowedHosts", "blocked.test"));
        provenance.TestSave();
        Check(!provenance.Saved && provenance.RecordedSteps.Count == 0 && DeniedStatus(provenance), "window Save checks drained clicks' original host even when current page is allowed");
        provenance.TestCancel();
        await provenance.TestWaitForUpdatesAsync();
        Policy();
        await page.GotoAsync("https://recorder.test/initial");

        var canceled = await OpenWindow(page);
        await page.ClickAsync("button");
        await Flush(page);
        Policy(("DisableTeach", 1));
        canceled.TestCancel();
        await canceled.TestWaitForUpdatesAsync();
        Check(!canceled.Saved && canceled.RecordedSteps.Count == 0, "Cancel remains unconditional after policy denial");
        Policy();
        var positive = await OpenWindow(page);
        positive.TestEditStep("Navigate", "https://recorder.test/positive");
        positive.TestSave();
        Check(positive.Saved && positive.RecordedSteps.Single().Target == "https://recorder.test/positive", "allowed manual Navigate Save remains supported");
        await positive.TestWaitForUpdatesAsync();
        StepEditorWindow.OnShow = null;
        Check(System.Windows.MessageBox.Messages.All(m => !m.Contains("private-url") && !m.Contains("blocked.test")), "policy UI never exposes raw denied URLs in diagnostic dialogs");
    }

    private static bool DeniedStatus(VisualRecorderWindow window) =>
        window.TestStatus == "Teach recording is disabled by your organization. Unsaved steps were cleared.";

    private static async Task<VisualRecorderWindow> OpenWindow(IPage page)
    {
        var window = new VisualRecorderWindow(page);
        window.TestLoad();
        await WaitUntil(() => window.TestToggleEnabled);
        return window;
    }

    internal static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    internal static Task Flush(IPage page) => page.EvaluateAsync("""() => window.__launchFlowTeachDispatchV1('{"type":"hello"}')""");

    private static void Refused(Action action, string name)
    {
        var denied = false;
        try { action(); }
        catch (InvalidOperationException ex) { denied = ex.Message == "Teach recording is disabled by your organization."; }
        Check(denied, name);
    }

    internal static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL " + name);
        Console.WriteLine("PASS " + name);
    }
}
