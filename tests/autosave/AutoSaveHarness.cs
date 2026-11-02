using System.Text.Json;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Data.Sqlite;
using System.Windows.Controls;

var folder = Path.Combine(Path.GetTempPath(), "launchflow-autosave-integration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
Environment.SetEnvironmentVariable("XDG_DATA_HOME", folder);
const string password = "SyntheticAutosaveOnly-73!";
var vault = VaultDatabase.Current;
if (!Path.GetFullPath(vault.DatabasePath).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar,
        StringComparison.Ordinal))
    throw new InvalidOperationException("Autosave test must never use real user vault.");
vault.Initialize(password, BaseConfiguration());

int passed = 0;
int failed = 0;
void Run(string name, Action body)
{
    try
    {
        if (!vault.IsUnlocked && !vault.Unlock(password))
            throw new Exception("cannot unlock isolated synthetic vault");
        vault.ReplaceSnapshot(BaseConfiguration(), new Dictionary<Guid, SiteCredential>());
        body();
        Console.WriteLine("PASS " + name);
        passed++;
    }
    catch (Exception e)
    {
        Console.WriteLine("FAIL " + name + " (" + e.GetType().Name + "): " + e.Message);
        failed++;
    }
    finally
    {
        if (!vault.IsUnlocked) vault.Unlock(password);
    }
}
void Check(bool ok, string why)
{
    if (!ok) throw new InvalidOperationException(why);
}
Configuration ColdRead()
{
    vault.Lock();
    Check(vault.Unlock(password), "isolated vault must reopen");
    return new ConfigStore(vault).Load();
}
void WaitAndTick(AutoSaveFixture ui)
{
    Thread.Sleep(AutoSaveSchedule.DefaultDelay + TimeSpan.FromMilliseconds(90));
    ui.FireTimer();
}
void Sql(string statement)
{
    using var connection = new SqliteConnection($"Data Source={vault.DatabasePath}");
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = statement;
    command.ExecuteNonQuery();
}

try
{
    Run("actual dirty subscriptions -> timer -> unchanged SaveAll -> encrypted cold reopen", () =>
    {
        var ui = new AutoSaveFixture();
        var original = ui.Selected!;
        var worklist = ui.Configuration.Workflows;
        ui.NameBox.Text = "NOC dashboard";
        ui.KeepassEntryBox.Text = "Ops/Shared";
        Check(ui.Dirty && ui.TimerEnabled, "production text subscriptions did not mark dirty/arm timer");
        ui.FireTimer();
        Check(ui.Dirty && ColdRead().Workflows.Single().Name == "Original",
            "debounce was bypassed by early timer tick");
        WaitAndTick(ui);
        Check(!ui.Dirty && ui.SaveStatusLabel.Text == "All changes saved",
            "actual SaveAll did not clear dirty state on durable success");
        Check(ReferenceEquals(original, ui.Selected) && ReferenceEquals(worklist, ui.Configuration.Workflows),
            "production save broke selection/list identity");
        var read = ColdRead();
        Check(read.Workflows.Single().Name == "NOC dashboard" &&
              read.Workflows.Single().KeepassEntryPath == "Ops/Shared",
              "cold SQLite reopen lost website or KeePass changes");
        Check(read.AdditionalSettings!.ContainsKey("future_setting") &&
              read.Workflows.Single().Steps.Single().AdditionalSettings!.ContainsKey("future_action"),
              "unknown configuration/action fields were discarded");
    });
    Run("invalid late input keeps earlier edits out of live and cold vault", () =>
    {
        var ui = new AutoSaveFixture();
        ui.NameBox.Text = "Must not publish";
        ui.VaultTimeoutBox.Text = "999";
        WaitAndTick(ui);
        Check(ui.Dirty && ui.SaveStatusLabel.Text == "Save failed" &&
              ui.Selected!.Name == "Original" && ColdRead().Workflows.Single().Name == "Original",
              "partial invalid draft leaked into live or encrypted storage");
        Check(!ui.TimerEnabled, "failed validation auto-retries without a new edit");
        ui.VaultTimeoutBox.Text = "10";
        WaitAndTick(ui);
        Check(!ui.Dirty && ColdRead().Workflows.Single().Name == "Must not publish",
              "corrected input could not retry through real timer/SaveAll");
    });
    Run("different invalid workflow rejects whole snapshot before SQLite write", () =>
    {
        var ui = new AutoSaveFixture();
        ui.Configuration.Workflows.Add(new WebsiteWorkflow { Name = "Invalid other", StartUrl = "javascript:bad" });
        ui.NameBox.Text = "Uncommitted original";
        WaitAndTick(ui);
        Check(ui.Dirty && ui.Selected!.Name == "Original" &&
              ColdRead().Workflows.Single().Name == "Original",
              "validation published an earlier valid workflow despite unrelated invalid workflow");
    });
    Run("actual SQLite refusal blocks UI acknowledgment; explicit retry persists", () =>
    {
        var ui = new AutoSaveFixture();
        ui.NameBox.Text = "Durable on retry";
        Sql("CREATE TRIGGER deny_autosave BEFORE UPDATE ON objects WHEN NEW.id='config' BEGIN SELECT RAISE(ABORT, 'synthetic refusal'); END;");
        try
        {
            WaitAndTick(ui);
            Check(ui.Dirty && ui.Selected!.Name == "Original" &&
                  ui.SaveStatusLabel.Text == "Save failed" &&
                  ColdRead().Workflows.Single().Name == "Original",
                  "SQLite failure falsely acknowledged a save or changed live workflow");
            Check(!ui.TimerEnabled, "failed save created retry storm");
        }
        finally { Sql("DROP TRIGGER IF EXISTS deny_autosave;"); }
        Check(ui.ManualSave() && !ui.Dirty &&
              ColdRead().Workflows.Single().Name == "Durable on retry",
              "explicit retry did not cold-persist after genuine SQLite refusal");
    });
    Run("locked vault refuses timer/manual writes, without transient success", () =>
    {
        var ui = new AutoSaveFixture();
        ui.NameBox.Text = "Locked must not save";
        vault.Lock();
        WaitAndTick(ui);
        Check(ui.Dirty && !ui.ManualSave(), "locked vault accepted a write");
        Check(ColdRead().Workflows.Single().Name == "Original",
            "locked auto-save wrote to cold encrypted storage");
    });
    Run("credential input is excluded from dirty subscriptions and implicit save", () =>
    {
        var ui = new AutoSaveFixture();
        ui.UsernameBox.Text = "synthetic-private-email@example.invalid";
        ui.PasswordInput.Password = "never-persist-implicitly";
        Check(!ui.Dirty && !ui.TimerEnabled, "credential editor triggered implicit autosave");
        ui.NameBox.Text = "Non-secret change";
        WaitAndTick(ui);
        var read = ColdRead();
        Check(read.Workflows.Single().Name == "Non-secret change" &&
              !vault.HasCredential(read.Workflows.Single().Id) &&
              !System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(vault.DatabasePath))
                  .Contains("never-persist-implicitly", StringComparison.Ordinal),
              "save unexpectedly stored credential data");
    });
    Run("post-commit OS failure remains saved; reentrant UI edit remains dirty", () =>
    {
        var ui = new AutoSaveFixture();
        ui.StartupHook = () => {
            ui.StartupHook = null;
            ui.ShortcutBox.Text = "Ctrl+Shift+U";
            throw new InvalidOperationException("simulated Windows startup registration failure");
        };
        ui.NameBox.Text = "Committed before side-effect";
        WaitAndTick(ui);
        Check(ui.Dirty && ui.SaveStatusLabel.Text != "Save failed" &&
              ColdRead().Workflows.Single().Name == "Committed before side-effect",
              "post-commit platform error incorrectly classified as rejected write");
        Check(ui.TimerEnabled, "new edit during post-commit UI/platform effect was not scheduled");
        WaitAndTick(ui);
        Check(!ui.Dirty && ColdRead().GlobalShortcut == "Ctrl+Shift+U",
              "reentrant edit after commit was not saved on next timer");
    });
}
finally
{
    vault.Lock();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(folder, recursive: true); }
    catch { Console.WriteLine("NOTE disposable fixture cleanup pending"); }
}
Console.WriteLine($"RESULT autosave integration passed={passed} failed={failed}");
return failed == 0 ? 0 : 1;

static Configuration BaseConfiguration() => new()
{
    UiLanguage = "en",
    AdditionalSettings = new()
    {
        ["future_setting"] = JsonDocument.Parse("{\"nested\":42}").RootElement.Clone()
    },
    Workflows = new()
    {
        new WebsiteWorkflow
        {
            Name = "Original",
            Browser = "Edge",
            StartUrl = "https://example.test/",
            Steps = new()
            {
                new WorkflowStep
                {
                    Type = "FutureStep",
                    Target = "synthetic",
                    AdditionalSettings = new() {
                        ["future_action"] = JsonDocument.Parse("{\"id\":7}").RootElement.Clone()
                    }
                }
            }
        }
    }
};

partial class AutoSaveFixture
{
    private readonly ConfigStore _store = new(VaultDatabase.Current);
    private readonly ConfigurationSaveTransaction _saveTransaction;
    private readonly StartupRegistrationService _startupRegistration;
    private Configuration _configuration;
    private WebsiteWorkflow? _selected;
    private bool _loadingEditor;
    private bool _vaultLockInProgress = false, _windowsLocked = false, _exiting = false, _runInProgress = false;
    private bool _installerShutdownInProgress = false;
    private bool _monitorSelectionChanged = false;
    private bool _hasUnsavedChanges;
    private bool HasPendingEditorDialog => false;

    public Action? StartupHook;
    public readonly List<string> Logs = new();
    public readonly FakeControl NameBox = new(), StartUrlBox = new(), DestinationUrlBox = new();
    public readonly FakeControl BrowserCombo = new(), MonitorCombo = new(), EnabledCheck = new();
    public readonly FakeControl DelayBox = new(), VaultTimeoutBox = new(), LoginModeCombo = new();
    public readonly FakeControl UsernameContinueBox = new(), PasswordOriginBox = new();
    public readonly FakeControl UsernameSelectorBox = new(), PasswordSelectorBox = new(), SubmitSelectorBox = new();
    public readonly FakeControl CredentialOriginBox = new(), LoginSuccessSelectorBox = new(), LoginSuccessUrlBox = new();
    public readonly FakeControl LoginWaitBox = new(), KeepassEntryBox = new();
    public readonly FakeControl UsernameBox = new(), PasswordInput = new();
    public readonly FakeControl StartupCheck = new(), UnlockCheck = new(), LaunchCheck = new();
    public readonly FakeControl TrayCheck = new(), NotifyCheck = new(), SealProfilesCheck = new();
    public readonly FakeControl TaskSchedulerCheck = new(), UpdateFeedBox = new(), ShortcutBox = new();
    public readonly FakeControl KeePassDbBox = new(), KeePassKeyBox = new(), LanguageCombo = new();
    public readonly FakeControl AutoLoginCheck = new(), WorkflowList = new();
    public readonly FakeControl SaveStatusLabel = new(), SaveChangesButton = new();

    public AutoSaveFixture()
    {
        _configuration = _store.Load();
        _selected = _configuration.Workflows.Single();
        NameBox.Text = _selected.Name;
        StartUrlBox.Text = _selected.StartUrl;
        DestinationUrlBox.Text = _selected.DestinationUrl;
        BrowserCombo.SelectedItem = _selected.Browser;
        EnabledCheck.IsChecked = _selected.Enabled;
        KeepassEntryBox.Text = _selected.KeepassEntryPath ?? "";
        LoginWaitBox.Text = "60";
        DelayBox.Text = "5";
        VaultTimeoutBox.Text = "10";
        ShortcutBox.Text = _configuration.GlobalShortcut;
        LanguageCombo.SelectedItem = new ComboBoxItem { Tag = _configuration.UiLanguage };
        LoginModeCombo.SelectedIndex = WebsiteLoginMode.Index(_selected.LoginMode);
        _startupRegistration = new StartupRegistrationService(
            _ => StartupHook?.Invoke(), _ => StartupHook?.Invoke());
        var validator = new WorkflowConfigurationService(_store.SaveDurable, url =>
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) ||
                (u.Scheme != "https" && u.Scheme != "http"))
                throw new ArgumentException("Invalid test website URL");
        });
        _saveTransaction = new ConfigurationSaveTransaction(_store.SaveDurable, validator.Validate);
        AttachProductionDirtyEvents();
        StartAutoSave();
    }
    private static string BrowserSelection(FakeControl browser) =>
        browser.SelectedItem?.ToString() ?? "";
    private static string SelectedLoginMode(int index) => WebsiteLoginMode.FromIndex(index);
    private void ApplyUiLanguage() { }
    private void RefreshKeePassStatus() { }
    private void AppendLog(string message) => Logs.Add(message);

    public WebsiteWorkflow? Selected => _selected;
    public Configuration Configuration => _configuration;
    public bool Dirty => _hasUnsavedChanges;
    public bool TimerEnabled => _autoSaveTimer.IsEnabled;
    public void FireTimer() => _autoSaveTimer.Fire();
    public bool ManualSave() => SaveAll();
}
