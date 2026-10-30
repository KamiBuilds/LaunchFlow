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
Check(!autosave.Ready(t0.AddSeconds(3), true) && autosave.HasPendingChanges,
    "failed autosave retains dirty data without retry storms");
autosave.Changed(t0.AddSeconds(3));
Check(!autosave.Ready(t0.AddSeconds(3).AddMilliseconds(500), true) &&
      autosave.Ready(t0.AddSeconds(3).AddMilliseconds(901), true),
    "next user edit safely retries after a new debounce interval");
autosave.Saved();
Check(!autosave.HasPendingChanges && !autosave.Ready(t0.AddSeconds(10), true),
    "confirmed durable save clears the pending state");
Throws<ArgumentOutOfRangeException>(() => new AutoSaveSchedule(TimeSpan.Zero),
    "invalid zero auto-save delay is rejected");

Console.WriteLine("PASS all pure architecture contracts");
