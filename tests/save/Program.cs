using System.Text.Json;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Data.Sqlite;

static void Check(bool condition, string reason)
{
    if (!condition) throw new Exception("FAIL: " + reason);
    Console.WriteLine("PASS: " + reason);
}
static void WriteSql(string path, string sql)
{
    using var db = new SqliteConnection($"Data Source={path}");
    db.Open();
    using var cmd = db.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
}
var folder = Path.Combine(Path.GetTempPath(), "launchflow-save-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var path = Path.Combine(folder, "synthetic.db");
var password = "Synthetic!Vault9-Secure-Only";
try
{
    var vault = new VaultDatabase(path);
    var original = new Configuration
    {
        UiLanguage = "en",
        AdditionalSettings = new() {
            ["future_setting"] = JsonDocument.Parse("{\"nested\":42}").RootElement.Clone()
        },
        Workflows = new() {
            new WebsiteWorkflow
            {
                Name = "Original",
                StartUrl = "https://example.test/",
                Steps = new() {
                    new WorkflowStep {
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
    vault.Initialize(password, original);
    var store = new ConfigStore(vault);
    var live = store.Load();
    var workflow = live.Workflows[0];
    var worklist = live.Workflows;
    long generation = vault.SessionGeneration;
    var tx = new ConfigurationSaveTransaction(store.SaveDurable, cfg => {
        foreach (var w in cfg.Workflows)
            if (!Uri.TryCreate(w.StartUrl, UriKind.Absolute, out var u) || u.Scheme != "https")
                throw new ArgumentException("unsafe website URL");
    });

    var validation = tx.Commit(live,
        c => { c.UiLanguage = "nl"; c.Workflows[0].StartUrl = "javascript:invalid"; },
        () => true, generation, _ => throw new Exception("must not publish invalid values"));
    Check(validation.State == SaveTransactionState.ValidationRejected &&
        live.UiLanguage == "en" && workflow.StartUrl == "https://example.test/",
        "invalid late field does not change the live configuration");
    Check(store.Load().UiLanguage == "en", "invalid candidate leaves encrypted storage unchanged");

    // Reject an ACTUAL SQLite UPDATE. This is not a mocked writer exception.
    WriteSql(path, """
        CREATE TRIGGER deny_config_save BEFORE UPDATE ON objects
        WHEN NEW.id='config' BEGIN SELECT RAISE(ABORT, 'synthetic disk refusal'); END;
        """);
    var failed = tx.Commit(live, c => c.Workflows[0].Name = "Not persisted",
        () => true, generation, _ => throw new Exception("must not publish storage failure"));
    Check(failed.State == SaveTransactionState.StorageRejected &&
        workflow.Name == "Original", "real SQLite rejection prevents live publication");
    vault.Lock();
    Check(vault.Unlock(password), "vault reopened with synthetic master password");
    Check(store.Load().Workflows[0].Name == "Original",
        "cold encrypted reopen after SQLite rejection keeps previous version");
    WriteSql(path, "DROP TRIGGER deny_config_save;");
    generation = vault.SessionGeneration;

    var committed = tx.Commit(live, c => {
        c.UiLanguage = "pl";
        c.Workflows[0].Name = "Committed";
    }, () => true, generation, c => {
        live.UiLanguage = c.UiLanguage;
        workflow.Name = c.Workflows[0].Name;
    });
    Check(committed.State == SaveTransactionState.CommittedAndPublished &&
        ReferenceEquals(workflow, live.Workflows[0]) &&
        ReferenceEquals(worklist, live.Workflows),
        "successful encrypted transaction publishes in place with workflow identity");
    vault.Lock();
    Check(vault.Unlock(password), "vault cold-reopened after successful commit");
    var recovered = store.Load();
    Check(recovered.UiLanguage == "pl" &&
        recovered.Workflows[0].Name == "Committed" &&
        recovered.AdditionalSettings!.ContainsKey("future_setting") &&
        recovered.Workflows[0].Steps[0].AdditionalSettings!.ContainsKey("future_action"),
        "cold encrypted reopen matches committed values and preserves unknown fields");
    var oldGeneration = generation;
    var denied = tx.Commit(live, c => c.UiLanguage = "de",
        () => true, oldGeneration, _ => throw new Exception("stale authorization published"));
    Check(denied.State == SaveTransactionState.StorageRejected &&
        store.Load().UiLanguage == "pl",
        "old vault session cannot write after lock and re-unlock");

    var separateVault = new VaultDatabase(path);
    Check(separateVault.Unlock(password) && new ConfigStore(separateVault).Load().UiLanguage == "pl",
        "independent vault instance verifies disk bytes rather than only memory cache");
    separateVault.Lock();
    vault.Lock();
    Console.WriteLine("PASS: all real encrypted SQLite save integration tests");
}
finally
{
    try { Directory.Delete(folder, recursive: true); }
    catch { /* Fixture-only cleanup, never delete user data. */ }
}
