using LaunchFlow.Models;
using LaunchFlow.Services;

var tests = new List<(string Name, Func<Task> Body)>
{
    ("vault key and editor lock immediately while slow browser cleanup yields to the UI", async () =>
    {
        var ui = new UiFixture();
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Task.Run(async () => { var pending = ui.Lock(); returned.SetResult(); await pending; });
        await ui._runner.CloseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            Check(!VaultDatabase.Current.IsUnlocked && ui.Cleared, "key/editor remained unlocked during browser cleanup");
            Check(await Task.WhenAny(returned.Task, Task.Delay(300)) == returned.Task, "lock blocked the caller during browser cleanup");
            Check(!ui.Unlock(), "unlock allowed during pending profile cleanup");
        }
        finally { ui._runner.CloseRelease.TrySetResult(); await task.WaitAsync(TimeSpan.FromSeconds(3)); }
        Check(ProfileVaultService.Seals == 1, "profiles not sealed after browser cleanup");
    }),
    ("run buttons disable throughout a run and recover after cancellation", async () =>
    {
        var ui = new UiFixture();
        var run = ui.Run();
        await ui._runner.RunEntered.Task;
        try
        {
            Check(!ui.RunAllButton.IsEnabled && !ui.RunSelectedButton.IsEnabled && !ui.RunWorkspaceButton.IsEnabled && ui.StopButton.IsEnabled, "run controls allow duplicate actions");
            ui._runCancellation!.Cancel();
        }
        finally { ui._runner.RunRelease.TrySetResult(new()); await run; }
        Check(ui.RunAllButton.IsEnabled && ui.RunSelectedButton.IsEnabled && !ui.StopButton.IsEnabled, "controls not restored");
        Check(ui.Notifications.All(n => !n.Contains("Finished")), "cancelled run announced completion");
    }),
    ("workspace Run and Preview require the currently selected saved name", () =>
    {
        var ui = new UiFixture();
        var profile = new WorkspaceProfile { Name = "Morning" };
        ui._configuration.Workspaces.Add(profile);

        ui.RefreshControls();
        Check(!ui.RunWorkspaceButton.IsEnabled && !ui.PreviewWorkspaceButton.IsEnabled,
            "workspace actions enabled with no selected saved profile");

        ui.WorkspaceCombo.SelectedItem = profile;
        ui.WorkspaceNameBox.Text = "  morning ";
        ui.RefreshControls();
        Check(ui.RunWorkspaceButton.IsEnabled && ui.PreviewWorkspaceButton.IsEnabled,
            "valid selected workspace actions unavailable");

        ui.WorkspaceNameBox.Text = "Unsaved name";
        ui.RefreshControls();
        Check(!ui.RunWorkspaceButton.IsEnabled && !ui.PreviewWorkspaceButton.IsEnabled,
            "typing another profile name left a stale workspace action enabled");

        ui.WorkspaceNameBox.Text = "Morning";
        ui._runInProgress = true;
        ui.RefreshControls();
        Check(!ui.RunWorkspaceButton.IsEnabled && !ui.PreviewWorkspaceButton.IsEnabled,
            "active run allowed another workspace action");
        return Task.CompletedTask;
    }),
    ("modal vault prompt cannot reenter a run before engine IsRunning is set", async () =>
    {
        var ui = new UiFixture();
        VaultDatabase.Current.IsUnlocked = false;
        Task? duplicate = null;
        ui.PromptHook = () => { ui.PromptHook = null; duplicate = ui.Run(); };
        var first = ui.Run();
        await ui._runner.RunEntered.Task;
        try { Check(ui.Prompts == 1, "duplicate trigger opened a second vault prompt"); }
        finally { ui._runner.RunRelease.TrySetResult(new()); await first; if (duplicate is not null) await duplicate; }
    }),
    ("Test this site with no selection never falls through to Run all", async () =>
    {
        var ui = new UiFixture { _selected = null };
        ui._runner.RunRelease.SetResult(new());
        await ui.Run(selected: true);
        Check(ui._runner.RunCalls == 0, "missing selection ran unrelated workflows");
    }),
    ("workspace run uses enabled resolved IDs only and failure count is not announced as success", async () =>
    {
        var ui = new UiFixture();
        var disabled = new WebsiteWorkflow { Enabled = false };
        ui._configuration.Workflows.Add(disabled);
        var workspace = new WorkspaceProfile { Name = "NOC", WorkflowIds = new() { Guid.NewGuid(), disabled.Id, ui._configuration.Workflows[0].Id } };
        ui._configuration.Workspaces.Add(workspace);
        ui.WorkspaceCombo.SelectedItem = workspace;
        ui.WorkspaceNameBox.Text = workspace.Name;
        ui._runner.RunRelease.SetResult(new() { FailedCount = 1 });
        await ui.Run(workspace: workspace);
        Check(ui._runner.LastRun.Count == 1 && !ui._runner.IncludeDisabled, "workspace bypassed enabled/missing IDs");
        Check(ui.Notifications.Any(n => n.Contains("failed", StringComparison.OrdinalIgnoreCase)) && ui.Notifications.All(n => !n.Contains("Finished")), "failed workflows announced as success");
    }),
    ("encrypted import while KeePass is open cancels a detached workspace run", async () =>
    {
        var ui = new UiFixture();
        using var database = new SyntheticKeePassDatabase(ui);
        var old = ui._configuration.Workflows[0];
        var workspace = new WorkspaceProfile { Name = "Office", WorkflowIds = new() { old.Id } };
        ui._configuration.Workspaces.Add(workspace);
        ui.WorkspaceCombo.SelectedItem = workspace;
        ui.WorkspaceNameBox.Text = workspace.Name;
        ui.KeePassHook = () =>
        {
            ui._configuration.Workflows.Clear();
            ui._configuration.Workspaces.Clear();
            var imported = new WebsiteWorkflow { Name = "Imported", Enabled = false };
            ui._configuration.Workflows.Add(imported);
            ui._configuration.Workspaces.Add(new WorkspaceProfile
            {
                Name = "Office", WorkflowIds = new() { imported.Id }
            });
            ui._workspaceRevision++;
            ui.WorkspaceCombo.SelectedItem = null;
            ui.WorkspaceNameBox.Clear();
        };
        ui._runner.RunRelease.SetResult(new());
        await ui.Run(workspace: workspace);
        Check(ui._runner.RunCalls == 0 && ui._keepassMasterPassword is null,
            "stale pre-import workflow reached the execution engine or retained KeePass secret");
        Check(ui._configuration.Workflows.Single().Name == "Imported" &&
            ui._configuration.Workspaces.Single().WorkflowIds
                .SequenceEqual(new[] { ui._configuration.Workflows.Single().Id }),
            "canceled old run mutated imported workflow membership");
    }),
    ("import during KeePass cancels an old run-all request", async () =>
    {
        var ui = new UiFixture();
        using var database = new SyntheticKeePassDatabase(ui);
        ui.KeePassHook = () =>
        {
            ui._configuration.Workflows.Clear();
            ui._configuration.Workflows.Add(new WebsiteWorkflow { Name = "Replacement" });
            ui._workspaceRevision++;
        };
        ui._runner.RunRelease.SetResult(new());
        await ui.Run();
        Check(ui._runner.RunCalls == 0 && ui._keepassMasterPassword is null,
            "run-all dispatched a detached previous workflow after import");
    }),
    ("an unlock dialog open across Windows lock must not reauthorize the vault", () =>
    {
        var ui = new UiFixture();
        VaultDatabase.Current.IsUnlocked = false;
        ui.UnlockHook = () => { ui._windowsLocked = true; VaultDatabase.Current.Lock(); };
        Check(!ui.Unlock() && !VaultDatabase.Current.IsUnlocked && ui._master.Verifications == 0, "dialog authorized after Windows locked");
        return Task.CompletedTask;
    }),
    ("modeless Teach saves to its original workflow after selection changes", async () =>
    {
        var ui = new UiFixture();
        var original = ui._configuration.Workflows[0];
        var other = new WebsiteWorkflow();
        ui._configuration.Workflows.Add(other);
        var teaching = ui.Teach(original);
        ui._selected = other;
        ui.RecorderResult.SetResult(true);
        await teaching;
        Check(original.Steps.Count == 1 && other.Steps.Count == 0 && ui._store.Saves == 1, "Teach retargeted the selection");
    }),
    ("modeless Teach discards pending clicks when vault authorization disappears", async () =>
    {
        var ui = new UiFixture();
        var original = ui._configuration.Workflows[0];
        var teaching = ui.Teach(original);
        VaultDatabase.Current.Lock();
        ui.RecorderResult.SetResult(true);
        await teaching;
        Check(original.Steps.Count == 0 && ui._store.Saves == 0, "Teach attempted persistence after lock");
    }),
    ("vault revoked during a KeePass prompt prevents engine launch and clears the pending password", async () =>
    {
        var ui = new UiFixture();
        using var database = new SyntheticKeePassDatabase(ui);
        ui.KeePassHook = () => { ui._keepassMasterPassword = "fixture-only"; VaultDatabase.Current.Lock(); };
        ui._runner.RunRelease.SetResult(new());
        await ui.Run();
        Check(ui._runner.RunCalls == 0 && ui._keepassMasterPassword is null, "run launched or retained pending password after vault lock");
    }),
    ("policy denied credential status is unavailable, not an empty vault or KeePass fallback", () =>
    {
        var ui = new UiFixture();
        var workflow = ui._configuration.Workflows[0];
        workflow.KeepassEntryPath = "synthetic";
        VaultDatabase.Current.Credentials[workflow.Id] = new("old-user", "old-password");
        using var policy = EnterprisePolicy.UseProviderForTesting(() => new Dictionary<string, object?> { ["DisableCredentialStorage"] = 1 });
        Check(ui.Status(workflow).Contains("organization"), "status did not report organization denial");
        Check(!ui.NeedsKeePass(workflow), "denied local credential fell back to KeePass");
        ui.Load(workflow);
        Check(ui._loadedCredentialWorkflowId is null, "denied editor authorized a new credential");
        return Task.CompletedTask;
    }),
    ("credential save uses durable encrypted configuration storage", () =>
    {
        var ui = new UiFixture();
        var workflow = ui._configuration.Workflows[0];
        ui._selected = workflow;
        ui._loadedCredentialWorkflowId = workflow.Id;
        ui.UsernameBox.Password = "synthetic-user";
        ui.PasswordInput.Password = "synthetic-password";
        ui.SaveCredential();
        Check(ui._store.DurableSaves == 1 &&
            VaultDatabase.Current.Credentials[workflow.Id].Password == "synthetic-password",
            "credential success did not require a durable config write");
        return Task.CompletedTask;
    }),
    ("SSO workflows never request a KeePass password", () =>
    {
        var ui = new UiFixture();
        var workflow = ui._configuration.Workflows[0];
        workflow.AutoLogin = true; workflow.LoginMode = "SSO"; workflow.KeepassEntryPath = "synthetic";
        Check(!ui.NeedsKeePass(workflow), "SSO requested KeePass credentials");
        return Task.CompletedTask;
    }),
    ("disabled fill preserves an existing credential when saving blank replacement fields", () =>
    {
        var ui = new UiFixture();
        ui._selected = ui._configuration.Workflows[0];
        var id = ui._selected.Id;
        VaultDatabase.Current.Credentials[id] = new("old-user", "old-password");
        ui._loadedCredentialWorkflowId = id;
        using var policy = EnterprisePolicy.UseProviderForTesting(() => new Dictionary<string, object?> { ["DisableCredentialFill"] = 1 });
        ui.UsernameBox.Password = "replacement";
        ui.SaveCredential();
        Check(VaultDatabase.Current.Credentials[id].Username == "old-user" && ui._store.Saves == 0, "denied read overwrote saved credential");
        Check(MessageBox.Messages.Any(n => n.Contains("organization")), "policy save denial was not explained");
        return Task.CompletedTask;
    }),
    ("fresh automation policy after the KeePass modal prevents dispatch", async () =>
    {
        var ui = new UiFixture();
        var values = new Dictionary<string, object?>();
        using var policy = EnterprisePolicy.UseProviderForTesting(() => values);
        using var database = new SyntheticKeePassDatabase(ui);
        ui.KeePassHook = () => values["DisableAutomation"] = 1;
        ui._runner.RunRelease.SetResult(new());
        await ui.Run();
        Check(ui._runner.RunCalls == 0, "stale policy allowed run dispatch");
    }),
    ("fresh Teach policy discards pending modeless clicks and prevents repair launch", async () =>
    {
        var ui = new UiFixture();
        var values = new Dictionary<string, object?>();
        using var policy = EnterprisePolicy.UseProviderForTesting(() => values);
        var original = ui._configuration.Workflows[0];
        var pending = ui.Teach(original);
        values["DisableTeach"] = 1;
        ui.RecorderResult.SetResult(true);
        await pending;
        Check(original.Steps.Count == 0 && ui._store.Saves == 0, "Teach saved after policy revocation");
        await ui.Repair();
        Check(ui._runner.TeachCalls == 0, "repair launched under denied Teach policy");
    }),
    ("SSO modal rechecks cancellation, vault authorization and fresh host policy before approval", async () =>
    {
        foreach (var denial in new[] { "cancel", "lock", "policy" })
        {
            var ui = new UiFixture();
            var workflow = ui._configuration.Workflows[0];
            workflow.StartUrl = "https://site.example";
            ui._runCancellation = new();
            var values = new Dictionary<string, object?>();
            using var policy = EnterprisePolicy.UseProviderForTesting(() => values);
            MessageBox.Hook = () => { if (denial == "cancel") ui._runCancellation.Cancel(); else if (denial == "lock") VaultDatabase.Current.Lock(); else values["AllowedHosts"] = "site.example"; };
            Check(!await ui.Approve(workflow, "https://idp.example"), "SSO approved after " + denial);
            Check(workflow.ApprovedLoginOrigins.Count == 0 && ui._store.Saves == 0, "SSO persisted after " + denial);
        }
    }),
    ("mixed login attention and failed steps retain failure counts and select duplicate names by ID without rerun", async () =>
    {
        var ui = new UiFixture();
        var first = ui._configuration.Workflows[0];
        var second = new WebsiteWorkflow { Name = first.Name };
        ui._configuration.Workflows.Add(second);
        MessageBox.Result = MessageBoxResult.No;
        var result = new AutomationEngine.RunResult { FailedCount = 1 };
        result.LoginNeedsAttention.Add(new(first.Id, first.Name, "Synthetic sign-in attention"));
        result.StepsNeedAttention.Add(new(second.Id, second.Name, "Click"));
        ui._runner.RunRelease.SetResult(result);
        await ui.Run();
        Check(ui.Notifications.Any(n => n.Contains("1 workflow(s) failed")), "login attention hid failures");
        Check(ui.WorkflowStatusLabel.Text.Contains("failed", StringComparison.OrdinalIgnoreCase), "final status hid failure count");
        Check(ReferenceEquals(ui.WorkflowList.SelectedItem, second), "failed step selected duplicate workflow by name");
        Check(ui._runner.RunCalls == 1 && ui._runner.TeachCalls == 0, "repair auto-ran automation");
    }),
    ("workflow status is queued without blocking and stale callbacks cannot overwrite final status", () =>
    {
        var ui = new UiFixture();
        ui._runInProgress = true; ui._runStatusActive = true; ui._runCancellation = new();
        var method = typeof(UiFixture).GetMethod("QueueWorkflowStatus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Check(method is not null, "engine workflow status is not wired through a dispatcher method");
        method!.Invoke(ui, new object[] { "duplicate display name", AutomationEngine.WorkflowRunState.WaitingForSignIn });
        Check(ui.Dispatcher.Pending.Count == 1 && ui.WorkflowStatusLabel.Text == "", "status update blocked or bypassed dispatcher");
        ui.Dispatcher.Pending.Dequeue()();
        Check(ui.WorkflowStatusLabel.Text.Contains("Sign-in"), "workflow attention status missing");
        method.Invoke(ui, new object[] { "duplicate display name", AutomationEngine.WorkflowRunState.Completed });
        ui._runInProgress = false;
        ui.WorkflowStatusLabel.Text = "Final failed summary";
        ui.Dispatcher.Pending.Dequeue()();
        Check(ui.WorkflowStatusLabel.Text == "Final failed summary", "queued status overwrote final summary");
        return Task.CompletedTask;
    }),
    ("Auto None and SSO UI choices round-trip exact persisted login modes", () =>
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var index = typeof(UiFixture).GetMethod("LoginModeIndex", flags);
        var selected = typeof(UiFixture).GetMethod("SelectedLoginMode", flags);
        Check(index is not null && selected is not null, "exact login mode UI mapping is missing");
        foreach (var mode in new[] { "Auto", "None", "SSO" })
            Check((string)selected!.Invoke(null, new[] { index!.Invoke(null, new object[] { mode }) })! == mode, "login mode changed on save: " + mode);
        var xaml = System.Xml.Linq.XDocument.Load("LaunchFlow/MainWindow.xaml");
        var combo = xaml.Descendants().Single(e => (string?)e.Attribute(System.Xml.Linq.XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "LoginModeCombo");
        Check((string?)combo.Attribute("Visibility") != "Collapsed" && combo.Elements().Count() == 3, "SSO choice is hidden or missing");
        return Task.CompletedTask;
    }),
    ("fatal and dispatcher callbacks revoke keys but never seal potentially live profiles", () =>
    {
        foreach (var fatal in new[] { false, true })
        {
            _ = new UiFixture();
            var crash = new CrashFixture();
            var args = new FailureArgs();
            if (fatal) crash.FatalFailure(args); else crash.DispatcherFailure(args);
            Check(!VaultDatabase.Current.IsUnlocked, "crash retained vault authorization");
            Check(ProfileVaultService.Seals == 0, "crash callback sealed live profiles without an engine barrier");
            Check(MessageBox.Messages.All(n => !n.Contains("synthetic-private-error")), "crash exposed exception payload");
        }
        return Task.CompletedTask;
    }),
    ("KeePass entry selection rechecks policy after listing entries and cannot persist a denied fallback", async () =>
    {
        var ui = new UiFixture();
        ui._selected = ui._configuration.Workflows[0];
        var database = Path.GetTempFileName();
        try
        {
            ui.KeePassDbBox.Text = database;
            ui._keepassMasterPassword = "synthetic-list-only";
            var values = new Dictionary<string, object?>();
            using var policy = EnterprisePolicy.UseProviderForTesting(() => values);
            var pending = AsyncVoidTracker.Run(() => ui.ChooseEntry());
            await KeePassXcService.ListEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            values["DisableCredentialStorage"] = 1;
            KeePassXcService.ListRelease.SetResult(new() { "synthetic-entry" });
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Check(ui._store.Saves == 0 && string.IsNullOrEmpty(ui._selected.KeepassEntryPath), "KeePass selection persisted after policy denial");
            Check(ui._keepassMasterPassword is null, "KeePass retained password after policy denial");
        }
        finally { File.Delete(database); }
    }),
    ("positive login repair remains bound to the attention workflow ID across a modal selection change", async () =>
    {
        var ui = new UiFixture();
        var original = ui._configuration.Workflows[0];
        var other = new WebsiteWorkflow { Name = original.Name };
        ui._configuration.Workflows.Add(other);
        ui._selected = original;
        MessageBox.Hook = () => ui._selected = other;
        var result = new AutomationEngine.RunResult();
        result.LoginNeedsAttention.Add(new(original.Id, original.Name, "Synthetic attention"));
        ui._runner.RunRelease.SetResult(result);
        ui.RecorderResult.SetResult(true);
        await ui.Run();
        Check(ui._runner.LastTeachId == original.Id && other.Steps.Count == 0 && original.Steps.Count == 1, "repair switched workflow after its prompt");
        Check(ui._runner.RunCalls == 1, "repair reran the workflow");
    }),
    ("close barrier releases stalled run and Teach dispatch before UI observes them", async () =>
    {
        var ui = new UiFixture();
        ui._runner.CloseCancelsDispatch = true;
        var run = ui.Run();
        await ui._runner.RunEntered.Task;
        ui._activeTeachTask = ui._runner.PendingTeach.Task;
        var pending = ui.Lock();
        await ui._runner.CloseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!pending.IsCompleted && !VaultDatabase.Current.IsUnlocked && ProfileVaultService.Seals == 0, "lock bypassed a stalled dispatch barrier");
        ui._runner.CloseRelease.SetResult();
        await Task.WhenAll(run, pending).WaitAsync(TimeSpan.FromSeconds(3));
        Check(ui._runner.PendingTeach.Task.IsCanceled && ProfileVaultService.Seals == 1, "Teach dispatch was not observed after barrier cancellation");
    }),
    ("Teach start and post-acquisition continuation use fresh policy", async () =>
    {
        var ui = new UiFixture();
        ui._selected = ui._configuration.Workflows[0];
        var values = new Dictionary<string, object?> { ["DisableTeach"] = 1 };
        using var policy = EnterprisePolicy.UseProviderForTesting(() => values);
        await AsyncVoidTracker.Run(ui.Record).WaitAsync(TimeSpan.FromSeconds(3));
        Check(ui._runner.TeachCalls == 0 && ui.Recorder is null, "Teach started while denied");
        values.Clear();
        ui._runner.StallTeach = true;
        var pending = AsyncVoidTracker.Run(ui.Record);
        Check(ui._runner.TeachCalls == 1, "Teach fixture did not enter acquisition");
        values["DisableTeach"] = 1;
        ui._runner.PendingTeach.SetResult(new Microsoft.Playwright.FakePage());
        await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Check(ui.Recorder is null && ui._store.Saves == 0, "Teach continued under a stale policy snapshot");
    }),
    ("dispatcher recovery requests the coordinated barrier while fatal recovery leaves it untouched", async () =>
    {
        foreach (var fatal in new[] { true, false })
        {
            var ui = new UiFixture();
            var crash = new CrashFixture { MainWindow = new LaunchFlow.MainWindow(ui) };
            if (fatal) crash.FatalFailure(new()); else crash.DispatcherFailure(new());
            Check(!VaultDatabase.Current.IsUnlocked && ProfileVaultService.Seals == 0, "callback sealed without barrier");
            Check(ui._runner.CloseEntered.Task.IsCompleted == !fatal, "fatal callback started asynchronous cleanup or dispatcher skipped it");
            if (!fatal)
            {
                ui._runner.CloseRelease.SetResult();
                await ui._vaultLockTask.WaitAsync(TimeSpan.FromSeconds(3));
                Check(ProfileVaultService.Seals == 1, "dispatcher cleanup failed after barrier completed");
            }
        }
    }),
    ("synchronous cancellation reentrancy sees a revoked key and shares the owned lock task", async () =>
    {
        var ui = new UiFixture();
        ui._runCancellation = new();
        Task? reentered = null;
        bool revokedAtCallback = false;
        using var registration = ui._runCancellation.Token.Register(() =>
        {
            revokedAtCallback = !VaultDatabase.Current.IsUnlocked;
            reentered = ui.Lock();
        });
        var first = ui.Lock();
        ui._runner.CloseRelease.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(3));
        if (reentered is not null) await reentered.WaitAsync(TimeSpan.FromSeconds(3));
        Check(revokedAtCallback, "cancellation callback observed an authorized vault key");
        Check(ReferenceEquals(first, reentered) && ProfileVaultService.Seals == 1, "reentrant cancellation duplicated lock cleanup");
    }),
    ("denied credential reads clear cached KeePass secrets instead of making a fallback ready", () =>
    {
        var ui = new UiFixture();
        ui._keepassMasterPassword = "synthetic-cached-secret";
        using var policy = EnterprisePolicy.UseProviderForTesting(() => new Dictionary<string, object?> { ["DisableCredentialStorage"] = 1 });
        Check(ui.EnsureKeePass() && ui._keepassMasterPassword is null, "denied read retained a cached fallback secret");
        return Task.CompletedTask;
    }),
    ("close failure retains profiles and blocks admission until a repeated lock succeeds", async () =>
    {
        var ui = new UiFixture();
        ui._runner.CloseFailure = true;
        ui._runner.CloseRelease.SetResult();
        await ui.Lock();
        Check(ProfileVaultService.Seals == 0, "sealed profiles despite failed close barrier");
        Check(!VaultDatabase.Current.IsUnlocked && ui._vaultLockInProgress && !ui.Unlock(), "failed cleanup reopened admission");
        Check(ui.Notifications.Any(n => n.Contains("Browser cleanup did not complete")), "fixed cleanup warning missing");
        Check(ui.WorkflowStatusLabel.Text.Contains("Browser cleanup did not complete"), "cleanup warning disappears when tray notifications are disabled");
        var retry = typeof(UiFixture).GetField("RetryVaultLockButton");
        Check(retry is not null && ((Control)retry.GetValue(ui)!).Visibility == Visibility.Visible, "failed lock has no reachable retry control outside disabled editor");
        ui._runner.CloseFailure = false;
        await ui.Lock();
        Check(ProfileVaultService.Seals == 1 && !ui._vaultLockInProgress, "repeat lock failed to retry cleanup");
    }),
    ("overlapping locks close sessions before observing a stalled dispatched run", async () =>
    {
        var ui = new UiFixture();
        var run = ui.Run();
        await ui._runner.RunEntered.Task;
        var first = ui.Lock();
        var second = ui.Lock();
        try
        {
            Check(ReferenceEquals(first, second), "overlapping locks duplicated cleanup");
            Check(!VaultDatabase.Current.IsUnlocked && ui._runCancellation!.IsCancellationRequested, "lock did not revoke and cancel immediately");
            Check(ui._runner.CloseEntered.Task.IsCompleted, "close barrier waited for stalled UI run");
            Check(ProfileVaultService.Seals == 0, "sealed before barrier completed");
        }
        finally
        {
            ui._runner.RunRelease.TrySetResult(new());
            await ui._runner.CloseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            ui._runner.CloseRelease.TrySetResult();
            await Task.WhenAll(run, first, second);
        }
        Check(ProfileVaultService.Seals == 1 && !ui._vaultLockInProgress, "cleanup was not coalesced or gate remained held");
    })
};
var failed = 0;
foreach (var test in tests)
{
    try { await test.Body(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + ex.Message); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} UI behavioral seam tests passed (production method bodies; not WPF/native Windows)");
return failed == 0 ? 0 : 1;
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

partial class UiFixture
{
    public readonly AutomationEngine _runner = new();
    public readonly Configuration _configuration = new() { Workflows = new() { new WebsiteWorkflow() } };
    public WebsiteWorkflow? _selected = new();
    public bool _runStatusActive = false, _vaultCleanupFailed = false;
    public bool _hasUnsavedChanges = false;
    public bool _installerShutdownInProgress = false, _installerProfileSealFailed = false;
    public long _workspaceRevision;
    public readonly Control RetryVaultLockButton = new();
    public bool _windowsLocked = false, _vaultLockInProgress = false, _runInProgress = false, _teachingInProgress = false, _exiting = false;
    public CancellationTokenSource? _runCancellation;
    public Task<AutomationEngine.RunResult>? _activeRunTask = null;
    public Task<Microsoft.Playwright.IPage>? _activeTeachTask = null;
    public Task _vaultLockTask = Task.CompletedTask;
    public string? _keepassMasterPassword;
    public readonly Control StopButton = new() { IsEnabled = false }, RunAllButton = new(), RunSelectedButton = new(), RunWorkspaceButton = new(), PreviewWorkspaceButton = new(), TeachButton = new(), RepairButton = new(), MainTabs = new();
    public readonly Control WorkspaceCombo = new(), WorkspaceNameBox = new();
    public readonly Control WorkflowStatusLabel = new();
    public Action? PromptHook;
    public int Prompts;
    public bool Cleared;
    public readonly List<string> Notifications = new();
    public UiFixture() { VaultDatabase.Current.IsUnlocked = true; VaultDatabase.Current.Credentials.Clear(); ProfileVaultService.Seals = 0; KeePassXcService.Reset(); MessageBox.Hook = null; MessageBox.Result = MessageBoxResult.Yes; MessageBox.Messages.Clear(); }
    public readonly MasterPasswordService _master = new();
    public readonly ConfigStore _store = new();
    public bool _passwordsUnlocked = true;
    public DateTimeOffset _passwordSessionExpiresUtc = DateTimeOffset.MaxValue;
    public static readonly TimeSpan PasswordSessionDuration = TimeSpan.FromHours(1);
    public Action? UnlockHook;
    public readonly TaskCompletionSource<bool> RecorderResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public VisualRecorderWindow? Recorder;
    public bool PasswordSessionIsValid() => _passwordsUnlocked && VaultDatabase.Current.IsUnlocked;
    public void PasswordSessionTimer_Tick(object? sender, EventArgs e) { }
    public Task<bool> ShowRecorderAsync(VisualRecorderWindow recorder) { Recorder = recorder; return RecorderResult.Task; }
    public void RefreshSteps() { }
    public bool SaveAll() => true;
    public void RefreshControls() => UpdateRunButtons();
    public Action? KeePassHook;
    public bool EnsureKeePass() => EnsureKeePassPasswordForRun(_configuration.Workflows);
    public void ShowFromTray() { }
    public void MarkSaved() { }
    public bool IsLoaded => true;
    public static void AppendLog(string _) { }
    public void ClearCredentialEditor() { Cleared = true; UsernameBox.Clear(); PasswordInput.Clear(); _loadedCredentialWorkflowId = null; }
    public void LockPasswordManager(string _) { Cleared = true; _passwordsUnlocked = false; }
    public void NotifyTray(string title, string message, System.Windows.Forms.ToolTipIcon icon = System.Windows.Forms.ToolTipIcon.Info) => Notifications.Add(message);
    public void Record() => RecordSteps_Click(this, new RoutedEventArgs());
    public void ChooseEntry() => ChooseKeePassEntry_Click(this, new RoutedEventArgs());
    public readonly CredentialVault _vault = new();
    public Guid? _loadedCredentialWorkflowId;
    public readonly Control CredentialStatus = new(), UsernameBox = new(), PasswordInput = new(), AutoLoginCheck = new(), KeepassEntryBox = new(), WorkflowList = new();
    public readonly Control KeePassDbBox = new(), KeePassKeyBox = new(), SettingsTabs = new();
    public void SetKeePassDatabase(string path) => KeePassDbBox.Text = path;
    public readonly FakeDispatcher Dispatcher = new();
    public bool IsVisible => true;
    public void Hide() { }
    public static string MakeBrowserFriendlyMessage(Exception ex) => ex.GetType().Name;
}
sealed class UnlockWindow
{
    public required UiFixture Owner { get; init; }
    public string Password => "synthetic-fixture-only";
    public bool? ShowDialog() { Owner.Prompts++; Owner.PromptHook?.Invoke(); Owner.UnlockHook?.Invoke(); return true; }
}
sealed class SignInDecisionWindow
{
    public enum Decision { Later, Repair, Confirmed }
    public Decision Choice { get; set; } = Decision.Later;
    public object? Owner { get; set; }
    public SignInDecisionWindow(string website, string reason) { }
    public bool? ShowDialog() => true;
}
sealed class VisualRecorderWindow
{
    public object? Owner { get; init; }
    public VisualRecorderWindow(Microsoft.Playwright.IPage page) { }
    public List<WorkflowStep> RecordedSteps { get; } = new() { new WorkflowStep { Type = "Click", Target = "#synthetic" } };
}
sealed class SyntheticKeePassDatabase : IDisposable
{
    private readonly string _path = Path.GetTempFileName();
    public SyntheticKeePassDatabase(UiFixture ui)
    {
        ui._configuration.KeePassDatabasePath = _path;
        ui._configuration.Workflows[0].AutoLogin = true;
        ui._configuration.Workflows[0].KeepassEntryPath = "synthetic-entry";
    }
    public void Dispose() => File.Delete(_path);
}
sealed class SecretPromptWindow(string title, string explanation)
{
    public UiFixture? Owner { get; init; }
    public string Secret => title + explanation;
    public bool? ShowDialog() { Owner?.KeePassHook?.Invoke(); return true; }
}
sealed class KeePassEntryPickerWindow(List<string> entries)
{
    public object? Owner { get; init; }
    public string SelectedPath => entries[0];
    public bool? ShowDialog() { MessageBox.Hook?.Invoke(); return true; }
}
sealed class AsyncVoidTracker : SynchronizationContext
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override void OperationStarted() { }
    public override void OperationCompleted() => _done.TrySetResult();
    public override void Post(SendOrPostCallback callback, object? state) => ThreadPool.QueueUserWorkItem(_ => { try { callback(state); } catch (Exception ex) { _done.TrySetException(ex); } });
    public static Task Run(Action action)
    {
        var previous = Current;
        var tracker = new AsyncVoidTracker();
        SetSynchronizationContext(tracker);
        try { action(); } finally { SetSynchronizationContext(previous); }
        return tracker._done.Task;
    }
}
partial class CrashFixture { public object? MainWindow { get; set; } = null; }
sealed class FailureArgs
{
    public Exception Exception { get; } = new Exception("synthetic-private-error");
    public object ExceptionObject => Exception;
    public bool Handled { get; set; }
}
namespace LaunchFlow { sealed class MainWindow(UiFixture ui) { public void RequestCrashCleanup() => ui.RequestCrashCleanup(); } }
sealed class RoutedEventArgs : EventArgs { }
enum MessageBoxButton { OK, YesNo }
enum MessageBoxImage { Warning, Information, Error, Question }
enum MessageBoxResult { No, Yes }
static class MessageBox
{
    public static Action? Hook;
    public static MessageBoxResult Result = MessageBoxResult.Yes;
    public static readonly List<string> Messages = new();
    public static MessageBoxResult Show(string message, string title, MessageBoxButton button, MessageBoxImage icon) => Show(new object(), message, title, button, icon);
    public static MessageBoxResult Show(object owner, string message, string title, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult result = MessageBoxResult.No)
    { Messages.Add(message); Hook?.Invoke(); return Result; }
}
sealed class FakeDispatcher
{
    public readonly Queue<Action> Pending = new();
    public void BeginInvoke(Action callback) => Pending.Enqueue(callback);
    public Task<T> InvokeAsync<T>(Func<T> callback) => Task.FromResult(callback());
}
namespace Microsoft.Playwright { public interface IPage { } public sealed class FakePage : IPage { } }
enum Visibility { Visible, Collapsed }
sealed class Control
{
    public Visibility Visibility { get; set; } = Visibility.Collapsed;
    public bool IsEnabled { get; set; } = true;
    public string Text { get; set; } = "";
    public string Password { get; set; } = "";
    public bool? IsChecked { get; set; }
    public int SelectedIndex { get; set; }
    public object? SelectedItem { get; set; }
    public void Clear() { Text = ""; Password = ""; }
}

namespace System.Windows.Forms { public enum ToolTipIcon { Info, Error, Warning } }
namespace LaunchFlow.Services
{
    sealed class VaultDatabase
    {
        public static VaultDatabase Current { get; } = new();
        public bool IsUnlocked { get; set; } = true;
        public void Lock() => IsUnlocked = false;
        public readonly Dictionary<Guid, SiteCredential> Credentials = new();
        public bool HasCredential(Guid id) => Credentials.ContainsKey(id);
        public SiteCredential? ReadCredential(Guid id) => Credentials.GetValueOrDefault(id);
        public void SaveCredential(Guid id, SiteCredential credential) { EnterprisePolicy.Current.RequireCredentialStorage(); Credentials[id] = credential; }
        public void DeleteCredential(Guid id) => Credentials.Remove(id);
        public Dictionary<Guid, SiteCredential> ExportCredentials() => new(Credentials);
        public Configuration LoadConfiguration() => new();
    }
    sealed class MasterPasswordService
    {
        public bool IsConfigured => true;
        public int Verifications;
        public bool Verify(string password) { Verifications++; VaultDatabase.Current.IsUnlocked = true; return true; }
    }
    sealed class ConfigStore
    {
        public int Saves;
        public int DurableSaves;
        public void Save(Configuration configuration) { if (!VaultDatabase.Current.IsUnlocked) throw new Exception("fixture vault locked"); Saves++; }
        public void SaveDurable(Configuration configuration) { Save(configuration); DurableSaves++; }
    }
    static class ProfileVaultService
    {
        public static int Seals;
        public static void SealAll() => Interlocked.Increment(ref Seals);
        public static void UnsealAll() { }
    }
    static class KeePassXcService
    {
        public static SiteCredential? ReadEntry(string database, string entry, string? keyFile, string? password) => null;
        public sealed record DatabaseChoice(string Path);
        public static List<DatabaseChoice> FindDatabases() => new();
        public static bool IsCliAvailable() => true;
        public static TaskCompletionSource ListEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static TaskCompletionSource<List<string>> ListRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static void Reset() { ListEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); ListRelease = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public static Task<List<string>> ListEntriesAsync(string database, string key, string secret) { ListEntered.TrySetResult(); return ListRelease.Task; }
    }
    static class LoginHealth { public sealed record Health(bool NeedsAttention, string Reason); public static Task<Health> InspectAsync(Microsoft.Playwright.IPage page) => Task.FromResult(new Health(false, "")); }
    static class CrashLog { public static void Write(string label, Exception ex) { } }
    sealed class AutomationEngine
    {
        public sealed record LoginAttention(Guid WorkflowId, string WorkflowName, string Reason);
        public Task<bool> ConfirmSignedInAsync(WebsiteWorkflow workflow, CancellationToken ct = default) =>
            Task.FromResult(true);
        public sealed record StepAttention(Guid WorkflowId, string WorkflowName, string StepType);
        public enum WorkflowRunState { Running, WaitingForSignIn, Completed, Failed, Cancelled }
        public Task<Microsoft.Playwright.IPage> GetPageForRecorderAsync(WebsiteWorkflow workflow) { LastTeachId = workflow.Id; TeachCalls++; return StallTeach ? PendingTeach.Task : Task.FromResult<Microsoft.Playwright.IPage>(new Microsoft.Playwright.FakePage()); }
        public int TeachCalls;
        public Guid? LastTeachId = null;
        public bool StallTeach = false, CloseCancelsDispatch = false;
        public readonly TaskCompletionSource<Microsoft.Playwright.IPage> PendingTeach = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public sealed class RunResult
        {
            public List<LoginAttention> LoginNeedsAttention { get; } = new();
            public List<StepAttention> StepsNeedAttention { get; } = new();
            public int FinishedCount { get; set; }
            public int FailedCount { get; set; }
        }
        public bool IsRunning;
        public int RunCalls;
        public List<WebsiteWorkflow> LastRun = new();
        public bool IncludeDisabled;
        public readonly TaskCompletionSource RunEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<RunResult> RunRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CloseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CloseRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<RunResult> RunAsync(IEnumerable<WebsiteWorkflow> workflows, CancellationToken ct, bool includeDisabled, Configuration configuration, string? keepassMasterPassword)
        {
            RunCalls++; IsRunning = true; LastRun = workflows.ToList(); IncludeDisabled = includeDisabled; RunEntered.TrySetResult();
            try { return await RunRelease.Task; } finally { IsRunning = false; }
        }
        public bool CloseFailure;
        public async Task CloseSessionsAsync() { CloseEntered.TrySetResult(); await CloseRelease.Task; if (CloseFailure) throw new Exception("synthetic close fault"); if (CloseCancelsDispatch) { RunRelease.TrySetCanceled(); PendingTeach.TrySetCanceled(); } }
    }
}
