using LaunchFlow;
using LaunchFlow.Models;
using LaunchFlow.Services;
using System.Reflection;

static void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
var root = Path.Combine(Path.GetTempPath(), "launchflow-handoff-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("XDG_DATA_HOME", root);
try
{
    var method = typeof(App).GetMethod("TryCreateAndVerifyVault", BindingFlags.Static | BindingFlags.NonPublic);
    Require(method is not null, "Setup handoff contract missing.");
    bool Invoke(string password)
    {
        try { return (bool)method!.Invoke(null, [VaultDatabase.Current, password, new Configuration(), null])!; }
        catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
    }
    VaultDatabase.Reset();
    Require(!Invoke("aZ7!aZ7!") && VaultDatabase.Calls.Count == 0, "Expected validation reached initialization.");
    Console.WriteLine("PASS expected_validation_has_no_storage_side_effects");
    const string eligible = "é漢🙂Z7! Keep exact ";
    VaultDatabase.Reset(); Require(Invoke(eligible), "Eligible setup refused.");
    Require(string.Join(",", VaultDatabase.Calls) == "live.Initialize,live.Lock,cold.New,cold.Unlock,cold.Lock,live.Unlock", "Readiness preceded cold verification/revocation.");
    Require(VaultDatabase.Inputs.All(p => p == eligible), "Handoff changed password representation.");
    Console.WriteLine("PASS initialize_revoke_independent_cold_verify_then_live_unlock");
    var trace = File.ReadAllText(StartupDiagnostics.FilePath);
    Require(trace.Contains("VaultRevokeCreatedSession") && trace.Contains("VaultColdRevoke") && trace.Contains("VaultOpenVerifiedSession"), "Actual handoff did not bracket all verification/key-lifetime operations.");
    Console.WriteLine("PASS actual_handoff_key_lifetime_phase_brackets");
    foreach (var stage in new[] { "live.Initialize", "cold.Unlock", "cold.Lock", "live.Unlock" })
    {
        VaultDatabase.Reset(); var failure = new ArgumentException("SYNTHETIC-PRIVATE-SENTINEL"); VaultDatabase.Failures[stage] = failure;
        Exception? observed = null; try { Invoke(eligible); } catch (Exception e) { observed = e; }
        Require(ReferenceEquals(observed, failure), "Unexpected failure category/identity replaced.");
        Require(VaultDatabase.Calls.Count(c => c == "live.Initialize") == 1 && VaultDatabase.Calls.Last() == "live.Lock", "Failure retried or left live key published.");
        Require(!VaultDatabase.Calls.Contains("Delete"), "Failure deleted durable material.");
    }
    Console.WriteLine("PASS operational_failures_preserve_category_and_never_retry_or_delete");
    VaultDatabase.Reset();
    var primary = new IOException("SYNTHETIC-PRIVATE-SENTINEL"); var secondary = new InvalidOperationException("SYNTHETIC-PRIVATE-SENTINEL");
    VaultDatabase.Failures["cold.Unlock"] = primary; VaultDatabase.Failures["cold.Lock"] = secondary;
    AggregateException? graph = null; try { Invoke(eligible); } catch (AggregateException e) { graph = e; }
    Require(graph is not null && graph.InnerExceptions.Contains(primary) && graph.InnerExceptions.Contains(secondary), "Unwind replaced primary operation.");
    Console.WriteLine("PASS primary_and_cleanup_failure_graph");
    // Regenerated exact InitializeVault body: prove the application, not only its helper, uses the contract.
    VaultDatabase.Reset(); SetupWindow.Submissions.Enqueue("aZ7!aZ7!"); SetupWindow.Submissions.Enqueue(eligible);
    Require(App.StartForTest(), "Correctable startup validation did not allow a corrected submission.");
    Require(VaultDatabase.Inputs.All(p => p == eligible) && VaultDatabase.Calls.Contains("cold.New"), "Actual App bypassed validation/cold helper.");
    Require(SetupWindow.Last!.ChosenPassword == "", "Actual App retained the setup snapshot.");
    Console.WriteLine("PASS actual_app_correctable_validation_and_cold_handoff");
    VaultDatabase.Reset(); SetupWindow.Submissions.Enqueue(eligible);
    var operational = new ArgumentException("SYNTHETIC-PRIVATE-SENTINEL"); VaultDatabase.Failures["live.Initialize"] = operational;
    Exception? propagated = null; try { App.StartForTest(); } catch (Exception error) { propagated = error; }
    Require(ReferenceEquals(propagated, operational) && SetupWindow.Last!.ChosenPassword == "", "Actual App mislabeled an operational failure or retained input.");
    Console.WriteLine("PASS actual_app_operational_failure_propagates_without_reclassification");
    VaultDatabase.Reset(); VaultRecoveryService.Pending = true;
    Require(!App.StartForTest() && VaultDatabase.Calls.Count == 0, "Actual startup inspected or initialized an interrupted preservation set.");
    VaultRecoveryService.Pending = false;
    Console.WriteLine("PASS actual_app_incomplete_preservation_blocks_storage_and_setup");
    // Block the real logger and repeat the successful handoff against explicit storage adapters.
    var log = StartupDiagnostics.FilePath; if (File.Exists(log)) File.Delete(log); Directory.CreateDirectory(log);
    VaultDatabase.Reset(); Require(Invoke(eligible), "Blocked logger broke successful handoff.");
    Console.WriteLine("PASS blocked_logger_does_not_break_handoff_adapter");
    return 0;
}
catch (Exception e) { Console.Error.WriteLine("FAIL " + e.Message); return 1; }
finally { Directory.Delete(root, true); }

namespace LaunchFlow { public partial class App { } }
namespace LaunchFlow.Models
{
    public class Configuration
    {
        public bool StartWithWindows { get; set; }
        public bool RunOnUnlock { get; set; }
        public bool RunOnAppStart { get; set; }
        public bool MinimizeToTray { get; set; }
        public int VaultLockMinutes { get; set; }
        public bool SealBrowserProfilesOnLock { get; set; }
        public bool PreferTaskSchedulerAutostart { get; set; }
    }
    public class SiteCredential { }
}
namespace System.Windows.Markup { public class XamlParseException : Exception { } }
namespace LaunchFlow.Services
{
    // Explicit test-only storage adapter, not native WPF, SQLite or crypto execution.
    public sealed class VaultDatabase
    {
        public static VaultDatabase Current { get; } = new();
        public static List<string> Calls { get; } = [];
        public static List<string> Inputs { get; } = [];
        public static Dictionary<string, Exception> Failures { get; } = [];
        public string DatabasePath => "synthetic-owned-fixture";
        public bool Exists => false;
        public bool IsUnlocked => Calls.LastOrDefault() == "live.Unlock";
        public string DataDirectory => "synthetic-owned-directory";
        public VaultStorageState InspectStorage() { Calls.Add("live.Inspect"); return VaultStorageState.Absent; }
        public string UpgradeLegacyVault(string password) => throw new Exception("Upgrade must not run.");
        private readonly string _name = "live";
        private VaultDatabase() { }
        public VaultDatabase(string path) { _name = "cold"; Call("New"); }
        public static void Reset() { Calls.Clear(); Inputs.Clear(); Failures.Clear(); }
        private void Call(string stage) { var id = _name + "." + stage; Calls.Add(id); if (Failures.TryGetValue(id, out var e)) throw e; }
        public void Initialize(string password, Configuration configuration, Dictionary<Guid, SiteCredential>? credentials = null) { Inputs.Add(password); Call("Initialize"); }
        public bool Unlock(string password) { Inputs.Add(password); Call("Unlock"); return true; }
        public void Lock() => Call("Lock");
    }
}
