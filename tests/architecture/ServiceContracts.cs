using LaunchFlow.Models;
using LaunchFlow.Services;

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAIL " + description);
    Console.WriteLine("PASS " + description);
}

static void Throws<TException>(Action action, string description) where TException : Exception
{
    try { action(); }
    catch (TException) { Console.WriteLine("PASS " + description); return; }
    throw new Exception("FAIL " + description);
}

var urls = new List<string>();
var persisted = new List<Configuration>();
var service = new WorkflowConfigurationService(
    c => persisted.Add(c),
    uri =>
    {
        urls.Add(uri);
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https"))
            throw new ArgumentException("Unsupported URL.");
    });
var workflow = new WebsiteWorkflow
{
    StartUrl = "https://app.example.test/",
    DestinationUrl = "https://app.example.test/dashboard",
    CredentialOrigin = "https://login.example.test",
    PasswordCredentialOrigin = "https://login.example.test/password"
};
var config = new Configuration { Workflows = new() { workflow } };
service.Save(config);
Check(persisted.Count == 1 && ReferenceEquals(persisted[0], config),
    "valid configuration reaches injected durable writer once");
Check(urls.Count == 4, "all optional configured origins and destinations are validated");

workflow.PasswordCredentialOrigin = "http://login.example.test/insecure";
Throws<ArgumentException>(() => service.Save(config),
    "insecure password origin is rejected");
Check(persisted.Count == 1, "validation failure never calls persistence");

workflow.PasswordCredentialOrigin = "";
workflow.CredentialOrigin = "http://login.example.test";
Throws<ArgumentException>(() => service.Save(config),
    "insecure username origin is rejected");
Check(persisted.Count == 1, "username origin violation never persists");

workflow.CredentialOrigin = "https://login.example.test";
workflow.StartUrl = "javascript:alert(1)";
Throws<ArgumentException>(() => service.Save(config),
    "URL validator is enforced for website start");
Check(persisted.Count == 1, "invalid website start never persists");

workflow.StartUrl = "https://app.example.test/";
Throws<ArgumentNullException>(() => service.Save(null!),
    "null configuration is refused");
var writeFailure = new WorkflowConfigurationService(
    _ => throw new IOException("storage unavailable"),
    _ => { });
Throws<IOException>(() => writeFailure.Save(config),
    "durable storage failures propagate instead of appearing saved");

var actions = new List<string>();
var registrations = new StartupRegistrationService(
    enabled => actions.Add("task:" + enabled),
    enabled => actions.Add("run:" + enabled));
registrations.Apply(true, true);
Check(actions.SequenceEqual(new[] { "run:False", "task:True" }),
    "scheduler preference disables Run-key before enabling scheduled task");
actions.Clear();
registrations.Apply(true, false);
Check(actions.SequenceEqual(new[] { "task:False", "run:True" }),
    "Run-key preference disables scheduler before enabling Run-key");
actions.Clear();
registrations.Apply(false, false);
Check(actions.SequenceEqual(new[] { "task:False", "run:False" }),
    "disabled autostart removes both mechanisms");
var registrationFailure = new StartupRegistrationService(
    _ => throw new InvalidOperationException("denied"),
    _ => { });
Throws<InvalidOperationException>(() => registrationFailure.Apply(true, false),
    "platform registration failures are not silently marked successful");

var timeout = SessionLockPolicy.Timeout(10);
Check(!SessionLockPolicy.RequiresVaultLock(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59), timeout),
    "unlock before 10-minute lock threshold preserves session");
Check(SessionLockPolicy.RequiresVaultLock(TimeSpan.FromMinutes(10), timeout),
    "unlock exactly at threshold locks vault");
Check(SessionLockPolicy.RequiresVaultLock(TimeSpan.FromMinutes(11), timeout),
    "unlock beyond threshold locks vault");
Check(SessionLockPolicy.RequiresVaultLock(TimeSpan.FromSeconds(-1), timeout),
    "clock rollback fails closed");
Check(SessionLockPolicy.Timeout(0) == TimeSpan.FromMinutes(1) &&
      SessionLockPolicy.Timeout(1000) == TimeSpan.FromMinutes(120),
    "configured timeouts are bounded");
Throws<ArgumentOutOfRangeException>(() => SessionLockPolicy.RequiresVaultLock(TimeSpan.Zero, TimeSpan.Zero),
    "nonpositive lock policy threshold is rejected");

// Debounced edits only become eligible after the delay. No transient
// validation error is allowed to produce a save/retry storm.
var t0 = new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);
var autosave = new AutoSaveSchedule();
autosave.Changed(t0);
Check(autosave.HasPendingChanges && !autosave.Ready(t0.AddMilliseconds(899), true),
    "autosave waits for the debounce interval");
Check(!autosave.Ready(t0.AddSeconds(5), false),
    "locked or editing state defers autosave even after debounce");
Check(autosave.Ready(t0.AddMilliseconds(900), true),
    "autosave becomes eligible at the exact debounce threshold");
autosave.Attempted();
Check(!autosave.CanAttempt, "failed write does not continuously retry");
Check(!autosave.Ready(t0.AddSeconds(3), true) && autosave.HasPendingChanges,
    "failed autosave retains dirty data without retry storms");
autosave.Changed(t0.AddSeconds(3));
Check(autosave.CanAttempt, "new edits during an attempted save re-arm auto-save");
Check(!autosave.Ready(t0.AddSeconds(3).AddMilliseconds(500), true) &&
      autosave.Ready(t0.AddSeconds(3).AddMilliseconds(901), true),
    "next user edit safely retries after a new debounce interval");
autosave.Saved();
Check(!autosave.HasPendingChanges && !autosave.Ready(t0.AddSeconds(10), true),
    "confirmed durable save clears the pending state");
Throws<ArgumentOutOfRangeException>(() => new AutoSaveSchedule(TimeSpan.Zero),
    "invalid zero auto-save delay is rejected");

// Check the REAL isolated candidate/commit/publication contract, not a
// WPF SaveAll stub. Invalid drafts and rejected writes must leave live state intact.
var txLive = new Configuration
{
    UiLanguage = "en",
    Workflows = new() { new WebsiteWorkflow { Name = "Original", StartUrl = "https://valid.example.test" } },
    AdditionalSettings = new() { ["future_config"] = System.Text.Json.JsonDocument.Parse("{\"kept\":true}").RootElement.Clone() }
};
var liveWorkflow = txLive.Workflows[0];
var liveList = txLive.Workflows;
var txWrites = new List<Configuration>();
long expectedVaultGeneration = 12;
var tx = new ConfigurationSaveTransaction(
    (candidate, expected) =>
    {
        if (expected != expectedVaultGeneration) throw new InvalidOperationException("stale key session");
        txWrites.Add(candidate);
    },
    candidate =>
    {
        if (!candidate.Workflows[0].StartUrl.StartsWith("https://", StringComparison.Ordinal))
            throw new ArgumentException("unsafe URL");
    });
var rejectedDraft = tx.Commit(txLive,
    candidate => { candidate.UiLanguage = "pl"; candidate.Workflows[0].StartUrl = "javascript:bad"; },
    () => true, 12, candidate => throw new Exception("must not publish rejected draft"));
Check(rejectedDraft.State == SaveTransactionState.ValidationRejected &&
      txLive.UiLanguage == "en" && txLive.Workflows[0].StartUrl == "https://valid.example.test" &&
      txWrites.Count == 0, "invalid later value does not mutate live settings or reach storage");
var staleBefore = tx.Commit(txLive, candidate => candidate.UiLanguage = "pl", () => false,
    12, candidate => throw new Exception("must never publish"));
Check(staleBefore.State == SaveTransactionState.SupersededBeforeWrite &&
      txWrites.Count == 0, "superseded edit is refused before write");
var failureTx = new ConfigurationSaveTransaction(
    (_, _) => throw new IOException("synthetic disk refusal"), _ => { });
var failure = failureTx.Commit(txLive, c => c.Workflows[0].Name = "Unsaved",
    () => true, 12, _ => throw new Exception("must not publish failed storage"));
Check(failure.State == SaveTransactionState.StorageRejected &&
      txLive.Workflows[0].Name == "Original", "failed persistence retains original live model");
var newerRevision = false;
var race = tx.Commit(txLive, c => c.UiLanguage = "pl",
    () => !newerRevision, 12, _ => { });
Check(race.Published && !newerRevision, "positive publish control succeeds before race");
newerRevision = false;
var committedSuperseded = new ConfigurationSaveTransaction(
    (c, _) => { txWrites.Add(c); newerRevision = true; }, _ => { });
var late = committedSuperseded.Commit(txLive, c => c.UiLanguage = "nl", () => !newerRevision,
    12, _ => throw new Exception("stale commit must not publish"));
Check(late.State == SaveTransactionState.CommittedButSuperseded &&
      txLive.UiLanguage == "en", "durable but superseded revision is not falsely acknowledged");
newerRevision = false;
var applied = tx.Commit(txLive, candidate => candidate.Workflows[0].Name = "Saved",
    () => true, 12, candidate => liveWorkflow.Name = candidate.Workflows[0].Name);
Check(applied.State == SaveTransactionState.CommittedAndPublished &&
      ReferenceEquals(txLive.Workflows, liveList) &&
      ReferenceEquals(txLive.Workflows[0], liveWorkflow) &&
      liveWorkflow.Name == "Saved" && txLive.AdditionalSettings!.ContainsKey("future_config"),
      "committed draft publishes in place and preserves configuration extension data");
// Guard the MainWindow wiring mistake: publish(candidate) with draft.Apply would
// only re-touch the clone and leave live unchanged after a successful commit.
var liveOnly = new Configuration
{
    UiLanguage = "en",
    Workflows = new() { new WebsiteWorkflow { Name = "Live", StartUrl = "https://valid.example.test" } }
};
var liveOnlyWorkflow = liveOnly.Workflows[0];
var cloneTouched = false;
var publishLive = new ConfigurationSaveTransaction(
    (candidate, _) => Check(candidate.Workflows[0].Name == "Candidate", "writer must see drafted candidate"),
    _ => { });
var published = publishLive.Commit(liveOnly,
    candidate => candidate.Workflows[0].Name = "Candidate",
    () => true, 12,
    candidate =>
    {
        cloneTouched = ReferenceEquals(candidate.Workflows[0], liveOnlyWorkflow);
        liveOnlyWorkflow.Name = candidate.Workflows[0].Name;
        liveOnly.UiLanguage = "nl";
    });
Check(published.Published && !cloneTouched && liveOnlyWorkflow.Name == "Candidate" &&
      liveOnly.UiLanguage == "nl",
    "publication callback must update live identity, not only re-apply onto the candidate");
var tokenRejected = tx.Commit(txLive, c => c.UiLanguage = "nl", () => true, 11,
    _ => throw new Exception("stale session must never publish"));
Check(tokenRejected.State == SaveTransactionState.StorageRejected &&
      txLive.UiLanguage == "en", "stale vault session cannot commit through owned writer");
var dto = new ConfigurationEditDraft(null, "nl", false, false, false, true, true, true,
    false, null, 5, 10, "Ctrl+M", "", "", true);
dto.Apply(txLive);
Check(txLive.UiLanguage == "nl" && txLive.AdditionalSettings.ContainsKey("future_config"),
    "immutable draft applies known fields without dropping unknown configuration data");

var traySingle = RunOutcomeSummary.TrayDetail(0, 1, 0, "MFA required");
Check(traySingle == "MFA required", "single sign-in attention keeps the specific reason");
var trayMixed = RunOutcomeSummary.TrayDetail(1, 1, 1, "MFA required");
Check(trayMixed.Contains("1 workflow(s) failed") && trayMixed.Contains("1 sign-in"),
    "mixed failures keep the count summary instead of only the sign-in reason");
Check(RunOutcomeSummary.FinishedTray(1, "Ops") == "Finished “Ops”.",
    "single finished tray names the website");
Check(RunOutcomeSummary.FinishedTray(3, null).Contains("3 workflows"),
    "multi finished tray reports count");

var sessionNow = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
Check(PasswordSessionPolicy.IsActive(true, true, sessionNow.AddMinutes(5), sessionNow),
    "active password-manager session is accepted");
Check(!PasswordSessionPolicy.IsActive(true, true, sessionNow.AddSeconds(-1), sessionNow),
    "expired password-manager session is refused");
Check(!PasswordSessionPolicy.IsActive(true, false, sessionNow.AddHours(1), sessionNow),
    "password-manager session dies when the vault locks");
Check(PasswordSessionPolicy.RenewFrom(sessionNow) == sessionNow.Add(PasswordSessionPolicy.Duration),
    "password-manager renew uses the fixed duration");

Console.WriteLine("PASS all pure architecture contracts");
