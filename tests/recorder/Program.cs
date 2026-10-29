using System.Reflection;
using LaunchFlow;
using LaunchFlow.Services;
using Microsoft.Playwright;

if (args is ["--binding-api"])
{
    foreach (var method in typeof(IPage).GetMethods().Where(m => m.Name == "ExposeBindingAsync"))
        Console.WriteLine(method);
    return;
}

using var policyProvider = RecorderPolicyTests.Provider();
using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
await using var context = await browser.NewContextAsync();
await context.RouteAsync("https://recorder.test/**", route => route.FulfillAsync(new()
{
    ContentType = "text/html",
    Body = route.Request.Url.Contains("/replay", StringComparison.Ordinal)
        ? "<button onclick='window.wrong=(window.wrong||0)+1'>Wrong</button><button id='right' onclick='window.rightClicks=(window.rightClicks||0)+1'>Right</button>"
        : "<button>Local fixture</button>"
}));
var page = await context.NewPageAsync();
if (args is ["--legacy-script", var path])
{
    var messages = new List<string>();
    await page.ExposeBindingAsync("__testRecorder", (BindingSource _, string json) => { messages.Add(json); return true; });
    await page.SetContentAsync("<button id='typed-private-marker'>typed-private-marker</button>");
    await page.EvaluateAsync(File.ReadAllText(path));
    await page.ClickAsync("button");
    await page.EvaluateAsync("() => window.__testRecorder('{}')");
    Assert(!string.Join("", messages).Contains("typed-private-marker"), "automatic capture never persists echoed input in IDs or labels");
    return;
}

await page.GotoAsync("https://recorder.test/initial");
RecorderPolicyTests.Construction(page);
if (args is ["--policy-construction"]) return;
await RecorderPolicyTests.DrainPolicy(page);
if (args is ["--policy-drain"]) return;
await RecorderPolicyTests.NativeAcceptance(page);
if (args is ["--policy-native"]) return;
await RecorderPolicyTests.WindowPolicy(page);
if (args is ["--policy-window"]) return;

using (var gesture = new TeachRecorder(page))
{
    await Fixture(page, "<button onclick='window.activations=(window.activations||0)+1'>Target</button>");
    await gesture.SynchronizeAsync();
    await Flush(page);
    var box = (await page.Locator("button").BoundingBoxAsync())!;
    await page.Mouse.MoveAsync(box.X + box.Width / 2, box.Y + box.Height / 2);
    await page.Mouse.DownAsync();
    await page.Mouse.MoveAsync(700, 500);
    await page.Mouse.UpAsync();
    await Flush(page);
    Assert(await page.EvaluateAsync<int>("window.activations || 0") == 0, "fixture drag away does not activate the button");
    Assert(gesture.Drain().Count == 0, "canceled pointer gestures never become saved button clicks");
}

using var first = new TeachRecorder(page);
await first.SynchronizeAsync();
await Fixture(page, "<button id='go'>Go</button>");
await Flush(page);
await page.ClickAsync("#go");
await Flush(page);
Assert(first.Drain().Count == 1, "navigation installs click listener without reinjection");
using var second = new TeachRecorder(page);
await second.SynchronizeAsync();
await page.ClickAsync("#go");
await Flush(page);
Assert(first.Drain().Count == 1 && second.Drain().Count == 1, "independent recorder windows");

await page.EvaluateAsync("""
    () => {
      window.__testMessages = [];
      const dispatch = window.__launchFlowTeachDispatchV1;
      window.__launchFlowTeachDispatchV1 = json => { window.__testMessages.push(json); return dispatch(json); };
    }
    """);
await page.ClickAsync("#go");
await Flush(page);
first.Drain(); second.Drain();
var stale = await page.EvaluateAsync<string>("() => window.__testMessages.find(json => JSON.parse(json).type === 'Click')");
first.Pause(); second.Pause();
await page.ClickAsync("#go");
await Flush(page);
Assert(first.Drain().Count == 0 && second.Drain().Count == 0, "pause rejects already installed browser listeners immediately");
first.Resume(); second.Resume();
await first.SynchronizeAsync();
await page.EvaluateAsync("json => window.__launchFlowTeachDispatchV1(json)", stale);
Assert(first.Drain().Count == 0 && second.Drain().Count == 0, "resume rejects in-flight events from previous capture epoch");
await page.ClickAsync("#go");
await Flush(page);
Assert(first.Drain().Count == 1 && second.Drain().Count == 1, "resume captures once per window");

first.Pause();
await first.SynchronizeAsync();
await Fixture(page, "<button>After navigation</button>");
await Flush(page);
await page.ClickAsync("button");
await Flush(page);
Assert(first.Drain().Count == 0 && second.Drain().Count == 1, "navigation retains each window's paused state");
first.Dispose();
first.Resume();
await first.SynchronizeAsync();
await page.ClickAsync("button");
await Flush(page);
Assert(!first.IsCapturing && first.Drain().Count == 0 && second.Drain().Count == 1, "closing one window is terminal and does not stop another");
second.Dispose();
await second.SynchronizeAsync();

for (var i = 0; i < 12; i++)
{
    using var session = new TeachRecorder(page);
    await session.SynchronizeAsync();
    await page.ClickAsync("button");
    await Flush(page);
    Assert(session.Drain().Count == 1, $"repeated session {i + 1} captures exactly once");
}
await second.SynchronizeAsync();
Assert(await page.EvaluateAsync<int>("Object.keys(window).filter(k => k === '__launchFlowTeachDispatchV1').length") == 1,
    "repeated sessions reuse a single page binding");

using var privacy = new TeachRecorder(page);
await privacy.SynchronizeAsync();
await Fixture(page, """
    <input id="plain"><input id="password" type="password"><textarea></textarea>
    <select><option>typed-private-marker</option></select>
    <div contenteditable="true">typed-private-marker</div>
    <div role="textbox" tabindex="0">typed-private-marker</div>
    <button id="typed-private-marker" aria-label="typed-private-marker"><span>typed-private-marker</span></button>
    """);
await Flush(page);
await page.FillAsync("#plain", "typed-private-marker");
await page.FillAsync("#password", "typed-private-marker");
await page.FillAsync("textarea", "typed-private-marker");
foreach (var selector in new[] { "#plain", "#password", "textarea", "select", "[contenteditable]", "[role=textbox]" })
    await page.ClickAsync(selector);
await Flush(page);
var formClicks = privacy.Drain();
Assert(formClicks.Count == 6 && formClicks.All(target => !target.Contains("typed-private-marker")),
    "form-field clicks are captured without values, labels or private attributes");
foreach (var target in formClicks)
    Assert(await page.Locator(target).CountAsync() == 1, "recorded form click resolves to one element");
await page.ClickAsync("button span");
await Flush(page);
var targets = privacy.Drain();
Assert(targets.Count == 1 && !targets[0].Contains("typed-private-marker"), "automatic capture never persists echoed input in IDs or labels");
Assert(await page.Locator(targets[0]).CountAsync() == 1, "structural selector resolves the intended clickable ancestor");
await page.EvaluateAsync("() => document.querySelector('button').click()");
await Flush(page);
Assert(privacy.Drain().Count == 0, "synthetic page events are ignored");
foreach (var malformed in new[] { "{", "null", "[]", "{}", "{\"type\":\"Click\",\"epoch\":\"bad\"}", new string('x', 9000) })
    await page.EvaluateAsync("json => window.__launchFlowTeachDispatchV1(json)", malformed);
await page.EvaluateAsync("() => window.__launchFlowTeachDispatchV1(null)");
Assert(privacy.Drain().Count == 0, "null browser binding payload is ignored");
Assert(privacy.Drain().Count == 0, "untrusted malformed binding payloads are ignored");
await page.EvaluateAsync("""
    async () => {
      const state = JSON.parse(await window.__launchFlowTeachDispatchV1('{"type":"hello"}'));
      const session = state.sessions[0];
      for (const target of ['#typed-private-marker', 'text=typed-private-marker', '[aria-label="typed-private-marker"]'])
        await window.__launchFlowTeachDispatchV1(JSON.stringify({type:'Click',id:session.id,epoch:session.epoch,target}));
    }
    """);
Assert(privacy.Drain().Count == 0, "binding rejects forged text, ID and aria selectors containing private values");
privacy.Dispose();
await privacy.SynchronizeAsync();

var saveWindow = new VisualRecorderWindow(page);
saveWindow.TestLoad();
await WaitUntil(() => saveWindow.TestToggleEnabled);
await page.ClickAsync("button");
await Flush(page);
saveWindow.TestSave();
Assert(saveWindow.Saved && saveWindow.RecordedSteps.Count == 1, "save freezes and drains pending clicks synchronously");
saveWindow.TestCancel();
Assert(saveWindow.Saved && saveWindow.RecordedSteps.Count == 1, "late close does not mutate an already saved snapshot");
saveWindow.TestClear();
Assert(saveWindow.RecordedSteps.Count == 1, "late clear cannot mutate a terminal saved snapshot");
await Flush(page);
await page.ClickAsync("button");
await Flush(page);
saveWindow.TestTick();
Assert(saveWindow.RecordedSteps.Count == 1, "closed saved window cannot append late clicks");

var cancelWindow = new VisualRecorderWindow(page);
cancelWindow.TestLoad();
cancelWindow.TestCancel();
await cancelWindow.TestWaitForUpdatesAsync();
await page.ClickAsync("button");
await Flush(page);
cancelWindow.TestTick();
Assert(!cancelWindow.Saved && cancelWindow.RecordedSteps.Count == 0, "cancel while startup is awaiting cannot resurrect capture");

var emptyWindow = new VisualRecorderWindow(page);
emptyWindow.TestLoad();
await WaitUntil(() => emptyWindow.TestToggleEnabled);
emptyWindow.TestSave();
Assert(!emptyWindow.Saved, "empty save keeps window open");
await emptyWindow.TestWaitForUpdatesAsync();
await page.ClickAsync("button");
await Flush(page);
emptyWindow.TestSave();
Assert(emptyWindow.Saved && emptyWindow.RecordedSteps.Count == 1, "recording resumes after an empty save");

var raceWindow = new VisualRecorderWindow(page);
raceWindow.TestLoad();
await WaitUntil(() => raceWindow.TestToggleEnabled);
await page.ClickAsync("button");
await Flush(page);
raceWindow.TestToggle();
raceWindow.TestCancel();
await raceWindow.TestWaitForUpdatesAsync();
raceWindow.TestTick();
Assert(!raceWindow.Saved && raceWindow.RecordedSteps.Count == 0, "cancel wins over an in-flight toggle and discards pending actions");
var removeWindow = new VisualRecorderWindow(page);
removeWindow.TestLoad();
await WaitUntil(() => removeWindow.TestToggleEnabled);
await page.ClickAsync("button");
await Flush(page);
removeWindow.TestTick();
removeWindow.TestSelectFirst();
await page.ClickAsync("button");
await Flush(page);
removeWindow.TestRemove();
Assert(removeWindow.RecordedSteps.Count == 1, "remove retains selection when pending clicks refresh the list");
removeWindow.TestCancel();
await removeWindow.TestWaitForUpdatesAsync();

var bulkWindow = new VisualRecorderWindow(page);
bulkWindow.TestLoad();
await WaitUntil(() => bulkWindow.TestToggleEnabled);
await page.ClickAsync("button");
await page.ClickAsync("button");
await Flush(page);
bulkWindow.TestTick();
bulkWindow.TestSelectAll();
Assert(bulkWindow.TestSelectedCount == 2,
    "Select all marks every recorded action for editing or removal");
bulkWindow.TestRemove();
Assert(bulkWindow.RecordedSteps.Count == 0,
    "Delete selected removes multiple captured actions without touching browser credentials");
bulkWindow.TestCancel();
await bulkWindow.TestWaitForUpdatesAsync();

using (var scoped = new TeachRecorder(page))
{
    await scoped.SynchronizeAsync();
    var popup = await context.NewPageAsync();
    await Fixture(popup, "<button>Popup</button>");
    Assert(await popup.EvaluateAsync<bool>("typeof window.__launchFlowTeachDispatchV1 === 'function'"),
        "shared context bridge is installed in new pages without reinjection");
    await Flush(popup);
    await popup.ClickAsync("button");
    await Flush(popup);
    Assert(scoped.Drain().Count == 0, "popup clicks never become wrong-target main-page steps");
    await popup.CloseAsync();
}

var targetWindow = new VisualRecorderWindow(page);
targetWindow.TestLoad();
await WaitUntil(() => targetWindow.TestToggleEnabled);
var otherPage = await context.NewPageAsync();
await otherPage.GotoAsync("https://recorder.test/other-tab");
await Flush(otherPage);
await otherPage.ClickAsync("button");
await Flush(otherPage);
targetWindow.TestTick();
Assert(targetWindow.TestStatus?.Contains("not recorded") == true,
    "unsupported target warning is visible even when no action was queued");
targetWindow.TestCaptureUrl();
Assert(targetWindow.RecordedSteps.Single().Type == "Navigate" && targetWindow.RecordedSteps.Single().Target == otherPage.Url,
    "explicit Add current page retains latest-tab URL behavior");
targetWindow.TestCancel();
await targetWindow.TestWaitForUpdatesAsync();
await otherPage.CloseAsync();

// Cross-site same-tab recording must add its URL automatically without
// recording OAuth query tokens or ever crossing to an unrelated popup tab.
await context.RouteAsync("https://other-workspace.test/**", route => route.FulfillAsync(new()
{
    ContentType = "text/html",
    Body = "<button id='cross'>Different website</button>"
}));
var crossPage = await context.NewPageAsync();
await crossPage.GotoAsync("https://recorder.test/cross-origin-start");
using (var cross = new TeachRecorder(crossPage))
{
    await cross.SynchronizeAsync();
    await crossPage.ClickAsync("button");
    await Flush(crossPage);
    var firstActions = cross.DrainActions();
    Assert(firstActions.Count == 1 && firstActions[0].Type == "Click",
        "same-origin initial click has no extra Navigate step");
    await crossPage.GotoAsync("https://other-workspace.test/second-site");
    await Flush(crossPage);
    await crossPage.ClickAsync("#cross");
    await Flush(crossPage);
    var moved = cross.DrainActions();
    Assert(moved.Count == 2 && moved[0] == ("Navigate", "https://other-workspace.test/second-site") &&
        moved[1].Type == "Click",
        "new website automatically adds Navigate before its first captured click");

    await crossPage.GotoAsync("https://recorder.test/callback?code=private-oauth-token");
    await Flush(crossPage);
    await crossPage.ClickAsync("button");
    await Flush(crossPage);
    Assert(cross.DrainActions().Count == 0 && cross.UnsupportedTargetObserved,
        "OAuth callback URL with credentials is never captured automatically");
}
await crossPage.CloseAsync();

using (var shadow = new TeachRecorder(page))
{
    await shadow.SynchronizeAsync();
    await Fixture(page, "<div id='host'></div>");
    await Flush(page);
    await page.EvaluateAsync("() => document.querySelector('#host').attachShadow({mode:'open'}).innerHTML = '<button>Shadow</button>'");
    await page.ClickAsync("#host button");
    await Flush(page);
    Assert(shadow.Drain().Count == 0 && shadow.UnsupportedTargetObserved,
        "shadow clicks are rejected rather than replaying against a retargeted host");
}

using (var pointer = new TeachRecorder(page))
{
    await pointer.SynchronizeAsync();
    await Fixture(page, "<button onpointerdown='this.remove()'>Navigate</button>");
    await Flush(page);
    await page.ClickAsync("button");
    await Flush(page);
    Assert(pointer.Drain().Count == 0, "target removal before a completed click never produces a phantom action");
    await Fixture(page, "<button onclick='this.remove()'>Complete then remove</button>");
    await Flush(page);
    await page.ClickAsync("button");
    await Flush(page);
    Assert(pointer.Drain().Count == 1, "completed click is captured before its click handler removes the target");
}

var pausedWindow = new VisualRecorderWindow(page);
pausedWindow.TestLoad();
await WaitUntil(() => pausedWindow.TestToggleEnabled);
await Fixture(page, "<button>Pending</button>");
await Flush(page);
await page.ClickAsync("button");
await Flush(page);
pausedWindow.TestPauseWithoutDrain();
pausedWindow.TestTick();
Assert(pausedWindow.RecordedSteps.Count == 1 && pausedWindow.TestStatus?.StartsWith("Paused") == true,
    "draining an admitted action preserves paused UI state");
pausedWindow.TestCancel();
await pausedWindow.TestWaitForUpdatesAsync();

using (var framed = new TeachRecorder(page))
{
    await framed.SynchronizeAsync();
    await Fixture(page, "<button>Main</button><iframe src='https://recorder.test/child-frame'></iframe>");
    await Flush(page);
    var frame = page.Frames.Single(f => f != page.MainFrame);
    await frame.WaitForSelectorAsync("button");
    await frame.EvaluateAsync("() => window.__launchFlowTeachDispatchV1('{\"type\":\"hello\"}')");
    await frame.ClickAsync("button");
    await frame.EvaluateAsync("() => window.__launchFlowTeachDispatchV1('{\"type\":\"hello\"}')");
    Assert(framed.Drain().Count == 0 && framed.UnsupportedTargetObserved,
        "new iframe is instrumented but cannot queue a wrong-main-frame action");
    await page.ClickAsync("button");
    await Flush(page);
    Assert(framed.Drain().Count == 1, "main frame continues recording after rejecting iframe actions");
}

using (var popupRecorder = new TeachRecorder(page))
{
    await popupRecorder.SynchronizeAsync();
    await Fixture(page, "<button onclick=\"window.open('about:blank')\">Open popup</button>");
    await Flush(page);
    var popup = await page.RunAndWaitForPopupAsync(() => page.ClickAsync("button"));
    await Fixture(popup, "<button>Popup target</button>");
    await Flush(popup);
    Assert(popupRecorder.Drain().Count == 1, "real popup opener is captured once on the original page");
    await popup.ClickAsync("button");
    await Flush(popup);
    Assert(popupRecorder.Drain().Count == 0 && popupRecorder.UnsupportedTargetObserved,
        "real window.open popup is instrumented and rejected without wrong-target persistence");
    await popup.CloseAsync();
}

using (var safeKeyboard = new TeachRecorder(page))
{
    await Fixture(page, "<input id='search' type='search'><input id='email' type='email'>" +
        "<input id='pass' type='password'><form><input id='login' type='text'>" +
        "<input type='password'></form>");
    await safeKeyboard.SynchronizeAsync();
    await page.FillAsync("#search", "private-search-text");
    await page.PressAsync("#search", "Enter");
    await page.PressAsync("#search", "Tab");
    await page.PressAsync("#search", "x"); // Ordinary typed characters must not be recorded.
    await page.FillAsync("#email", "private-email");
    await page.PressAsync("#email", "Enter");
    await page.FillAsync("#pass", "private-password");
    await page.PressAsync("#pass", "Enter");
    await page.PressAsync("#login", "Enter"); // Form contains a password input.
    await page.EvaluateAsync("() => window.__launchFlowTeachDispatchV1(JSON.stringify({type:'Press',id:'fake',epoch:0,target:'private-password'}))");
    await Flush(page);
    var actions = safeKeyboard.DrainActions();
    Assert(actions.Count == 2 && actions.All(a => a.Type == "Press") &&
        actions[0].Target == "Enter" && actions[1].Target == "Tab",
        "recorder captures safe search/navigation keys but ignores typed, email and password form keystrokes");
    Assert(!string.Join(" ", actions.Select(a => a.Target)).Contains("private-"),
        "keyboard events never store typed text or credential values");
}

using (var guardedKeys = new TeachRecorder(page))
{
    await Fixture(page, """
      <button id="first">First</button><button id="second">Second</button>
      <div id="open"></div><div id="closed"></div>
      <form id="account" onsubmit="return false"></form>
      <input id="externalSearch" type="search" form="account">
      <input id="externalPass" type="password" form="account">
      <button id="externalButton" type="button" form="account">External control</button>
      <form id="payment" autocomplete="cc-number"></form>
      <button id="externalPayment" form="payment">External payment</button>
      <input id="billing" autocomplete="cc-number">
      <input id="user" autocomplete="username">
      """);
    await guardedKeys.SynchronizeAsync();
    await page.FocusAsync("#first");
    await page.Keyboard.PressAsync("Tab");
    await page.Keyboard.PressAsync("Escape");
    await page.Keyboard.PressAsync("ArrowRight");
    await Flush(page);
    var buttons = guardedKeys.DrainActions();
    Assert(buttons.Select(a => a.Target).SequenceEqual(new[] { "Tab", "Escape", "ArrowRight" }) &&
        buttons.All(a => a.Type == "Press"),
        "Tab Escape and arrow-key navigation on ordinary buttons are not discarded");
    await page.FocusAsync("#second");
    await page.Keyboard.PressAsync("Shift+Tab");
    await Flush(page);
    Assert(guardedKeys.DrainActions().Count == 0 && guardedKeys.UnsupportedTargetObserved,
        "Shift+Tab is refused explicitly, never replayed as forward Tab");
    await page.EvaluateAsync("""
      () => {
        for (const mode of ['open','closed']) {
          const shadow = document.querySelector('#'+mode).attachShadow({mode});
          shadow.innerHTML = '<input type="password" id="secret"><input autocomplete="one-time-code" id="otp">';
          if (mode === 'closed') window.__closedTestRoot = shadow;
        }
      }
      """);
    foreach (var kind in new[] { "open", "closed" })
    {
        var frame = page.Locator("#" + kind);
        await frame.EvaluateAsync("host => { const secret = host.shadowRoot?.querySelector('input[type=password]'); if (secret) secret.focus(); }");
        if (kind == "open")
            await page.Keyboard.PressAsync("Enter");
        else
        {
            // Closed-root focus is a real trusted user key event retargeted to its host.
            await page.EvaluateAsync("() => window.__closedTestRoot.querySelector('#secret').focus()");
            await page.Keyboard.PressAsync("Enter");
            await page.EvaluateAsync("() => window.__closedTestRoot.querySelector('#otp').focus()");
            await page.Keyboard.PressAsync("Enter");
        }
    }
    await page.PressAsync("#externalSearch", "Enter");
    await page.PressAsync("#billing", "Enter");
    await page.PressAsync("#user", "Enter");
    // Isolate owner-based key capture from implicit HTML form submission first.
    // A separate regression below verifies the actual trusted submit click.
    await page.FocusAsync("#externalButton");
    await page.Keyboard.PressAsync("ArrowRight");
    await page.FocusAsync("#externalPayment");
    await page.Keyboard.PressAsync("ArrowRight");
    await Flush(page);
    var forbiddenActions = guardedKeys.DrainActions();
    Assert(forbiddenActions.Count == 0,
        "shadow-host, external-password-form, billing and username keystrokes never enter recorder; recorded actions=" +
        string.Join(",", forbiddenActions.Select(action => action.Type + ":" + action.Target)));
    // In Chromium, Enter in an input implicitly activates an associated submit
    // button with a trusted Click. Credential forms must reject that as well.
    await page.EvaluateAsync("() => { document.querySelector('#externalButton').type = 'submit'; }");
    await page.PressAsync("#externalSearch", "Enter");
    await page.ClickAsync("#externalButton");
    await Flush(page);
    Assert(guardedKeys.DrainActions().Count == 0,
        "implicit Enter submission and pointer clicks on a password form are not recorded");
}
var concurrent = Enumerable.Range(0, 8).Select(_ => new TeachRecorder(page)).ToArray();
await Task.WhenAll(concurrent.Select(s => s.SynchronizeAsync()));
await Fixture(page, "<button>Keyboard</button>");
await Flush(page);
await page.ClickAsync("button");
await page.FocusAsync("button");
await page.Keyboard.PressAsync("Enter");
await Flush(page);
Assert(concurrent.All(s => s.Drain().Count == 2), "concurrent initialization captures one pointer and one keyboard action per session");
foreach (var session in concurrent) session.Dispose();
await Task.WhenAll(concurrent.Select(s => s.SynchronizeAsync()));
var snapshot = await page.EvaluateAsync<string>("() => window.__launchFlowTeachDispatchV1('{\"type\":\"hello\"}')");
Assert(System.Text.Json.JsonDocument.Parse(snapshot).RootElement.GetProperty("sessions").GetArrayLength() == 0,
    "closed sessions are detached from the context-wide bridge");

var closingPage = await context.NewPageAsync();
await Fixture(closingPage, "<button>Before close</button>");
using (var closing = new TeachRecorder(closingPage))
{
    await closing.SynchronizeAsync();
    await Fixture(closingPage, "<button>Before close</button>");
    await Flush(closingPage);
    await closingPage.ClickAsync("button");
    await Flush(closingPage);
    await closingPage.CloseAsync();
    closing.Resume();
    Assert(!closing.IsCapturing && closing.Drain().Count == 1,
        "closing the original browser page is terminal but preserves admitted actions for Save");
}

var closedWindowPage = await context.NewPageAsync();
await Fixture(closedWindowPage, "<button>Before close</button>");
var closedWindow = new VisualRecorderWindow(closedWindowPage);
closedWindow.TestLoad();
await WaitUntil(() => closedWindow.TestToggleEnabled);
await closedWindowPage.CloseAsync();
closedWindow.TestTick();
Assert(closedWindow.TestStatus?.StartsWith("Browser page closed") == true && !closedWindow.TestToggleEnabled,
    "browser page closure is visible and cannot be resumed through the window");
closedWindow.TestCancel();
await closedWindow.TestWaitForUpdatesAsync();

await page.GotoAsync("https://recorder.test/replay");
var replayWindow = new VisualRecorderWindow(page);
replayWindow.TestLoad();
await WaitUntil(() => replayWindow.TestToggleEnabled);
await page.ClickAsync("#right");
await Flush(page);
replayWindow.TestSave();
Assert(replayWindow.Saved && replayWindow.RecordedSteps.Count == 1, "real window saves a completed click for engine replay");
await replayWindow.TestWaitForUpdatesAsync();
await page.ReloadAsync();
var execute = typeof(AutomationEngine).GetMethod("ExecuteStepAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
foreach (var step in replayWindow.RecordedSteps)
    await (Task)execute.Invoke(null, new object[] { page, step, CancellationToken.None })!;
Assert(await page.EvaluateAsync<int>("window.rightClicks || 0") == 1, "saved click replays through the real engine against the intended element");
Assert(await page.EvaluateAsync<int>("window.wrong || 0") == 0, "engine replay never clicks the neighboring fixture control");

var weak = CreateDisposedSession(page);
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
Assert(!weak.TryGetTarget(out _), "shared context bridge does not retain disposed session objects");

Console.WriteLine("All recorder behavior checks passed.");

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static WeakReference<TeachRecorder> CreateDisposedSession(IPage page)
{
    var session = new TeachRecorder(page);
    session.Dispose();
    return new WeakReference<TeachRecorder>(session);
}

static async Task Fixture(IPage page, string html)
{
    var url = "https://recorder.test/fixture/" + Guid.NewGuid().ToString("N");
    await page.RouteAsync(url, route => route.FulfillAsync(new() { ContentType = "text/html", Body = html }));
    await page.GotoAsync(url);
}

static Task Flush(IPage page) => page.EvaluateAsync("() => window.__launchFlowTeachDispatchV1('{\"type\":\"hello\"}')");
static async Task WaitUntil(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!condition()) await Task.Delay(10, timeout.Token);
}
static void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
}
