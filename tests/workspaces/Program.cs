using System.Text.Json;
using LaunchFlow.Models;

var tests = new List<(string Name, Action Body)>
{
    ("old config receives empty workspaces and automatic language without changing workflow IDs", () =>
    {
        const string old = """{"Workflows":[{"Id":"e0923ef5-7f08-4c63-8980-f438047968b7","Name":"Legacy","MonitorIndex":1}],"VaultLockMinutes":17,"GlobalShortcut":"Alt+F8"}""";
        var config = JsonSerializer.Deserialize<Configuration>(old)!;
        var workspaces = typeof(Configuration).GetProperty("Workspaces");
        Check(workspaces is not null, "Workspaces property missing");
        Check(((System.Collections.ICollection)workspaces!.GetValue(config)!).Count == 0, "old config workspaces default");
        Check((string?)typeof(Configuration).GetProperty("UiLanguage")?.GetValue(config) == "auto", "old config language default");
        Check(config.Workflows.Single().Id == Guid.Parse("e0923ef5-7f08-4c63-8980-f438047968b7"), "workflow identity changed");
        Check(config.VaultLockMinutes == 17 && config.GlobalShortcut == "Alt+F8", "old settings changed");
    }),
    ("workspace resolution keeps saved order, ignores missing and disabled IDs, and deduplicates", () =>
    {
        var a = new WebsiteWorkflow { Name = "A" };
        var b = new WebsiteWorkflow { Name = "disabled", Enabled = false };
        var c = new WebsiteWorkflow { Name = "C" };
        var config = new Configuration { Workflows = new() { a, b, c } };
        var profile = new WorkspaceProfile { WorkflowIds = new() { c.Id, Guid.NewGuid(), b.Id, a.Id, c.Id } };
        var method = typeof(WorkspaceProfile).GetMethod("Resolve");
        Check(method is not null, "Resolve missing");
        var resolved = ((IEnumerable<WebsiteWorkflow>)method!.Invoke(profile, new object[] { config })!).ToList();
        Check(resolved.SequenceEqual(new[] { c, a }), "resolution changed order or included disabled/missing/duplicate workflow");
        Check(profile.WorkflowIds.Count == 5, "resolution must not erase missing IDs (import/reconnect)");
    }),
    ("named workspace capture updates case-insensitively without changing profile identity", () =>
    {
        var config = new Configuration { Workflows = new() { new(), new() { Enabled = false } } };
        var method = typeof(WorkspaceProfile).GetMethod("Capture");
        Check(method is not null, "Capture missing");
        var first = (WorkspaceProfile)method!.Invoke(null, new object[] { config, "  Office  " })!;
        config.Workflows[0].Enabled = false;
        config.Workflows[1].Enabled = true;
        var updated = (WorkspaceProfile)method.Invoke(null, new object[] { config, "OFFICE" })!;
        Check(ReferenceEquals(first, updated) && config.Workspaces.Count == 1, "capture duplicated profile");
        Check(updated.Name == "OFFICE" && updated.WorkflowIds.SequenceEqual(new[] { config.Workflows[1].Id }), "capture must save enabled IDs only");
        foreach (var invalid in new[] { " ", new string('x', 65) })
        {
            try { method.Invoke(null, new object[] { config, invalid }); throw new Exception("invalid name accepted"); }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is ArgumentException) { }
        }
        Check(config.Workspaces.Count == 1, "invalid input mutated profiles");
    }),
    ("config round-trip retains all existing settings plus workspace, monitor, language and future fields", () =>
    {
        var original = JsonSerializer.SerializeToNode(new Configuration
        {
            Workflows = new() { new() { Name = "Synthetic", MonitorIndex = 4,
                ApprovedLoginOrigins = new(StringComparer.OrdinalIgnoreCase) { "https://login.example.test" },
                Steps = new() { new() { Type = "Click", Target = "#continue", Description = "Synthetic" } } } },
            StartWithWindows = true, RunOnUnlock = true, RunOnAppStart = true, MinimizeToTray = false,
            UnlockDelaySeconds = 19, VaultLockMinutes = 27, GlobalShortcut = "Alt+F9", ShortcutTipShown = true,
            TrayNotificationsEnabled = false, WelcomeChecklistShown = true, DiagnosticsLogEnabled = true,
            SealBrowserProfilesOnLock = false, PreferTaskSchedulerAutostart = true, WindowsHelloUnlockEnabled = true,
            UpdateFeedUrl = "https://example.test/releases", KeePassDatabasePath = "synthetic.kdbx", KeePassKeyFilePath = "synthetic.key"
        })!;
        original["UiLanguage"] = "nl";
        original["FutureSetting"] = JsonSerializer.SerializeToNode(new { Flags = new[] { 1, 2 }, Keep = true });
        original["Workflows"]![0]!["MonitorDeviceName"] = "DISPLAY-4";
        original["Workflows"]![0]!["FutureWorkflow"] = "retain";
        original["Workspaces"] = JsonSerializer.SerializeToNode(new[] { new { Id = Guid.NewGuid(), Name = "Office", WorkflowIds = new[] { Guid.NewGuid() }, FutureWorkspace = 42 } });
        var config = original.Deserialize<Configuration>()!;
        var roundtrip = JsonSerializer.SerializeToNode(config)!;
        Check(System.Text.Json.Nodes.JsonNode.DeepEquals(original, roundtrip), "roundtrip dropped or changed persisted fields");
    }),
    ("monitor selection follows device identity across reorder and falls back to primary when unplugged", () =>
    {
        System.Windows.Forms.Screen.AllScreens = new[]
        {
            new System.Windows.Forms.Screen { DeviceName = "DISPLAY-B", Bounds = new(0, 0, 800, 600) },
            new System.Windows.Forms.Screen { DeviceName = "DISPLAY-A", Primary = true, Bounds = new(800, 0, 1024, 768) }
        };
        var monitors = LaunchFlow.Services.MonitorService.GetMonitors();
        var resolve = typeof(LaunchFlow.Services.MonitorService).GetMethod("Resolve");
        Check(resolve is not null, "monitor Resolve missing");
        MonitorChoice Pick(WebsiteWorkflow workflow) => (MonitorChoice)resolve!.Invoke(null, new object[] { workflow, monitors })!;
        var workflow = new WebsiteWorkflow { MonitorIndex = 0, MonitorDeviceName = "display-a" };
        Check(Pick(workflow).Index == 1, "identity did not survive reorder or case");
        workflow.MonitorDeviceName = "unplugged";
        Check(Pick(workflow).Index == 1 && workflow.MonitorDeviceName == "unplugged", "missing device must use primary without destroying saved identity");
        workflow.MonitorDeviceName = "";
        Check(Pick(workflow).Index == 0, "legacy index ignored");
        workflow.MonitorIndex = 50;
        Check(Pick(workflow).Index == 1, "invalid legacy index must use primary, not last screen");
        workflow.MonitorIndex = -1;
        Check(Pick(workflow).Index == 1, "negative index fallback");
        var empty = (MonitorChoice)resolve!.Invoke(null, new object[] { workflow, Array.Empty<MonitorChoice>() })!;
        Check(empty.Bounds.Width > 0 && empty.Bounds.Height > 0, "no-screen fallback invalid");
        var noPrimary = new[] { new MonitorChoice { Index = 7, Bounds = new(0, 0, 640, 480) } };
        Check(ReferenceEquals(resolve!.Invoke(null, new object[] { workflow, noPrimary }), noPrimary[0]), "no-primary fallback");
        var clone = new WebsiteWorkflow { MonitorDeviceName = "DISPLAY-B" }.CloneWithoutCredential();
        Check(clone.MonitorDeviceName == "DISPLAY-B" && !clone.Enabled, "duplicate lost monitor identity or became enabled");
    }),
    ("English and Dutch selection is local, deterministic and falls back for unsupported translations", () =>
    {
        var type = typeof(Configuration).Assembly.GetType("LaunchFlow.Services.LocalizationService");
        Check(type is not null, "LocalizationService missing");
        var text = type!.GetMethod("Text")!;
        string Translate(string english, string? language) => (string)text.Invoke(null, new object?[] { english, language })!;
        var savedCulture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new("nl-NL");
            Check(Translate("Passwords", "auto") == "Wachtwoorden", "automatic Dutch failed");
            Check(Translate("Passwords", "en") == "Passwords", "explicit English ignored");
            Check(Translate("Passwords", "nl") == "Wachtwoorden", "explicit Dutch ignored");
            Check(Translate("Passwords", "unknown") == "Passwords", "unsupported explicit locale must fall back English");
            Check(Translate("not translated", "nl") == "not translated", "untranslated key fallback");
            System.Globalization.CultureInfo.CurrentUICulture = new("fr-FR");
            Check(Translate("Passwords", "auto") == "Passwords", "unsupported OS locale fallback");
            Check(Translate("Run workspace", "nl") == "Werkruimte starten", "workspace translation");
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = savedCulture; }
    }),
    ("locale code used for WPF accessibility agrees with translated text", () =>
    {
        var service = typeof(Configuration).Assembly.GetType("LaunchFlow.Services.LocalizationService")!;
        var resolve = service.GetMethod("ResolveLanguage");
        Check(resolve is not null, "ResolveLanguage missing");
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new("nl-BE");
            Check((string)resolve!.Invoke(null, new object?[] { "auto" })! == "nl", "OS Dutch locale mismatch");
            Check((string)resolve!.Invoke(null, new object?[] { "en" })! == "en", "explicit English locale mismatch");
            Check((string)resolve!.Invoke(null, new object?[] { "unsupported" })! == "en", "unsupported locale must be English");
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }),
    ("explicit null optional workspace collections are treated as empty for older exports", () =>
    {
        var config = JsonSerializer.Deserialize<Configuration>("""{"Workspaces":null}""")!;
        Check(config.Workspaces is { Count: 0 }, "null workspace collection not normalized");
        var profile = JsonSerializer.Deserialize<WorkspaceProfile>("""{"WorkflowIds":null}""")!;
        Check(!profile.Resolve(config).Any(), "null workflow IDs must resolve empty");
    })
};
tests.AddRange(WorkspaceCatalogTests.Cases());
tests.AddRange(ExitSafetyTests.Cases());
tests.AddRange(SafetyIntegrationTests.Cases());
tests.AddRange(WebsiteLoginModeTests.Cases());
tests.AddRange(ApplicationControlTests.Cases());
tests.AddRange(BrowserInstallationTests.Cases());

var failed = 0;
foreach (var test in tests)
{
    try { test.Body(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + ex.Message); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} tests passed (linked production; simulated Screen enumeration)");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
