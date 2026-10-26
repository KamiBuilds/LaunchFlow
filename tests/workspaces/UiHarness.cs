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
        var workspace = new WorkspaceProfile { WorkflowIds = new() { Guid.NewGuid(), disabled.Id, ui._configuration.Workflows[0].Id } };
        ui._runner.RunRelease.SetResult(new() { FailedCount = 1 });
        await ui.Run(workspace: workspace);
        Check(ui._runner.LastRun.Count == 1 && !ui._runner.IncludeDisabled, "workspace bypassed enabled/missing IDs");
        Check(ui.Notifications.Any(n => n.Contains("failed", StringComparison.OrdinalIgnoreCase)) && ui.Notifications.All(n => !n.Contains("Finished")), "failed workflows announced as success");
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
        ui.KeePassHook = () => { ui._keepassMasterPassword = "fixture-only"; VaultDatabase.Current.Lock(); };
        ui._runner.RunRelease.SetResult(new());
        await ui.Run();
        Check(ui._runner.RunCalls == 0 && ui._keepassMasterPassword is null, "run launched or retained pending password after vault lock");
    }),
    ("overlapping lock requests share cleanup and wait for cancelled automation before closing sessions", async () =>
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
            Check(!ui._runner.CloseEntered.Task.IsCompleted, "browser cleanup raced an active engine operation");
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
    public bool _windowsLocked = false, _vaultLockInProgress = false, _runInProgress = false, _teachingInProgress = false, _exiting = false;
    public CancellationTokenSource? _runCancellation;
    public Task<AutomationEngine.RunResult>? _activeRunTask = null;
    public Task? _activeTeachTask = null;
    public Task _vaultLockTask = Task.CompletedTask;
    public string? _keepassMasterPassword;
    public readonly Control StopButton = new() { IsEnabled = false }, RunAllButton = new(), RunSelectedButton = new(), RunWorkspaceButton = new(), TeachButton = new(), RepairButton = new(), MainTabs = new();
    public readonly Control WorkflowStatusLabel = new();
    public Action? PromptHook;
    public int Prompts;
    public bool Cleared;
    public readonly List<string> Notifications = new();
    public UiFixture() { VaultDatabase.Current.IsUnlocked = true; ProfileVaultService.Seals = 0; }
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
    public Action? KeePassHook;
    public bool EnsureKeePassPasswordForRun(IReadOnlyList<WebsiteWorkflow> _) { KeePassHook?.Invoke(); return true; }
    public void ShowFromTray() { }
    public static void AppendLog(string _) { }
    public void ClearCredentialEditor() => Cleared = true;
    public void LockPasswordManager(string _) { Cleared = true; _passwordsUnlocked = false; }
    public void NotifyTray(string title, string message, System.Windows.Forms.ToolTipIcon icon = System.Windows.Forms.ToolTipIcon.Info) => Notifications.Add(message);
    public Task OfferRepairForLoginAttentionAsync(IReadOnlyList<AutomationEngine.LoginAttention> _) => Task.CompletedTask;
}
sealed class UnlockWindow
{
    public required UiFixture Owner { get; init; }
    public string Password => "synthetic-fixture-only";
    public bool? ShowDialog() { Owner.Prompts++; Owner.PromptHook?.Invoke(); Owner.UnlockHook?.Invoke(); return true; }
}
sealed class VisualRecorderWindow
{
    public object? Owner { get; init; }
    public VisualRecorderWindow(Microsoft.Playwright.IPage page) { }
    public List<WorkflowStep> RecordedSteps { get; } = new() { new WorkflowStep { Type = "Click", Target = "#synthetic" } };
}
enum MessageBoxButton { OK }
enum MessageBoxImage { Warning, Information }
static class MessageBox { public static void Show(object owner, string message, string title, MessageBoxButton button, MessageBoxImage icon) { } }
namespace Microsoft.Playwright { public interface IPage { } public sealed class FakePage : IPage { } }
sealed class Control { public bool IsEnabled { get; set; } = true; public string Text { get; set; } = ""; }

namespace System.Windows.Forms { public enum ToolTipIcon { Info, Error, Warning } }
namespace LaunchFlow.Services
{
    sealed class VaultDatabase
    {
        public static VaultDatabase Current { get; } = new();
        public bool IsUnlocked { get; set; } = true;
        public void Lock() => IsUnlocked = false;
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
        public void Save(Configuration configuration) { if (!VaultDatabase.Current.IsUnlocked) throw new Exception("fixture vault locked"); Saves++; }
    }
    static class ProfileVaultService
    {
        public static int Seals;
        public static void SealAll() => Interlocked.Increment(ref Seals);
        public static void UnsealAll() { }
    }
    static class CrashLog { public static void Write(string label, Exception ex) { } }
    sealed class AutomationEngine
    {
        public sealed class LoginAttention { public string WorkflowName { get; set; } = ""; }
        public sealed class RunResult
        {
            public List<LoginAttention> LoginNeedsAttention { get; } = new();
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
        public async Task CloseSessionsAsync() { CloseEntered.TrySetResult(); await CloseRelease.Task; }
    }
}
