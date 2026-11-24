using System.Reflection;
using LaunchFlow.Services;

var root = Path.Combine(Path.GetTempPath(), "launchflow-diagnostics-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("XDG_DATA_HOME", root);
static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
try
{
    Require(Path.GetFullPath(StartupDiagnostics.FilePath).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Fixture log isolation failed.");
    const string sentinel = "SYNTHETIC-PRIVATE-PATH-PASSWORD-SENTINEL";
    var primary = new ArgumentException(sentinel, new IOException(sentinel));
    var aggregate = new AggregateException(sentinel, primary, new UnauthorizedAccessException(sentinel));
    StartupDiagnostics.Record(StartupDiagnostics.Stage.ProcessStarted);
    StartupDiagnostics.Record(StartupDiagnostics.Stage.StartupException, aggregate);
    var text = File.ReadAllText(StartupDiagnostics.FilePath);
    Require(!text.Contains(sentinel), "Secret sentinel emitted.");
    Require(text.Contains("runtime=") && text.Contains("mvid=") && text.Contains("arch="), "Nonsecret runtime identity missing.");
    Console.WriteLine("PASS runtime_identity");
    Require(text.Contains("AggregateException") && text.Contains("ArgumentException") && text.Contains("IOException") && text.Contains("UnauthorizedAccessException"), "Typed aggregate graph incomplete.");
    Console.WriteLine("PASS typed_bounded_aggregate_graph_and_sentinel_exclusion");
    Exception deep = new ArgumentException(sentinel);
    for (var i = 0; i < 100; i++) deep = new InvalidOperationException(sentinel, deep);
    StartupDiagnostics.Record(StartupDiagnostics.Stage.StartupException, deep);
    StartupDiagnostics.Record(StartupDiagnostics.Stage.StartupException,
        new AggregateException(Enumerable.Range(0, 100).Select(_ => new PRIVATE_TYPE_SENTINEL())));
    text = File.ReadAllText(StartupDiagnostics.FilePath);
    Require(text.Contains("bounded") && text.Contains("OtherException") && !text.Contains("PRIVATE_TYPE_SENTINEL") && !text.Contains(sentinel), "Deep/wide graph bounds or unknown type allowlist failed.");
    Require(text.Split('\n').All(line => line.Length < 4096), "One exception graph exceeded its bounded representation.");
    Console.WriteLine("PASS deep_wide_graph_bounded_and_unknown_type_names_excluded");
    Require(Enum.GetNames<StartupDiagnostics.Stage>().Contains("VaultCreateDisposeResources"), "Disposal stage missing.");
    var admission = typeof(StartupDiagnostics).GetNestedType("Admission", BindingFlags.NonPublic);
    var record = typeof(StartupDiagnostics).GetMethod("RecordAdmission", BindingFlags.Static | BindingFlags.NonPublic);
    Require(admission is not null && record is not null, "Typed admission diagnostic API missing.");
    foreach (var name in new[] { "UiAcceptable", "UiMeetsMinimum", "ConfirmationEqual", "HandoffOrdinalEqual", "DbMeetsMinimum" })
        record!.Invoke(null, new[] { Enum.Parse(admission!, name), (object)true });
    text = File.ReadAllText(StartupDiagnostics.FilePath);
    Require(text.Contains("UiAcceptable=True") && !text.Contains(sentinel), "Admission output unsafe or missing.");
    Console.WriteLine("PASS typed_admission_booleans");
    var db = new VaultDatabase();
    db.TestCreationStage("VaultCreateAdmission"); db.TestCreationStage("VaultCreateDisposeResources"); db.TestCreationStage("VaultCreateComplete");
    db.TestCreationStage(sentinel); db.TestCreationStage("VaultReady"); db.TestCreationStage("0");
    text = File.ReadAllText(StartupDiagnostics.FilePath);
    Require(text.Contains("VaultCreateAdmission") && text.Contains("VaultCreateDisposeResources") && text.Contains("VaultCreateComplete"), "Optional creation hook emitted no required stages.");
    Require(!text.Contains(sentinel) && !text.Contains("VaultReady"), "Optional hook accepts unknown or non-creation stage.");
    Console.WriteLine("PASS optional_hook_fixed_creation_allowlist");
    // Invalid enum and unknown creation stages must never become arbitrary output.
    StartupDiagnostics.Record((StartupDiagnostics.Stage)int.MaxValue);
    Require(!File.ReadAllText(StartupDiagnostics.FilePath).Contains("2147483647"), "Unallowlisted stage emitted.");
    for (var i = 0; i < 3000; i++) StartupDiagnostics.Record(StartupDiagnostics.Stage.StartupException, aggregate);
    Require(new FileInfo(StartupDiagnostics.FilePath).Length <= 256 * 1024 && new FileInfo(StartupDiagnostics.FilePath + ".previous").Length <= 256 * 1024, "Rotation exceeds bound.");
    File.Delete(StartupDiagnostics.FilePath); Directory.CreateDirectory(StartupDiagnostics.FilePath);
    StartupDiagnostics.Record(StartupDiagnostics.Stage.ProcessStarted, aggregate);
    record!.Invoke(null, new[] { Enum.Parse(admission!, "UiAcceptable"), (object)true });
    Console.WriteLine("PASS invalid_stage_rotation_and_blocked_sink_fail_open");
    return 0;
}
catch (Exception e) { Console.Error.WriteLine("FAIL " + e.Message); return 1; }
finally { Directory.Delete(root, true); }

namespace System.Windows.Markup { public class XamlParseException : Exception { } }
internal sealed class PRIVATE_TYPE_SENTINEL : Exception { }
