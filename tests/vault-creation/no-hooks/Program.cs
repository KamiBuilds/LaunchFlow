using System.Reflection;
using System.Security.Cryptography;
using LaunchFlow.Services;
using Microsoft.Data.Sqlite;

foreach (var name in new[] { "CreationOperationForTesting", "SensitiveBufferForTesting", "TraceCreationStage" })
{
    if (typeof(VaultDatabase).GetMethod(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is not null)
        throw new InvalidOperationException("Optional test/diagnostic hook survived omitted implementation.");
    Console.WriteLine("PASS erased optional hook " + name);
}
var root = Path.Combine(Path.GetTempPath(), "lf-no-hooks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var path = Path.Combine(root, "vault.db");
var password = "Aa!9-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
var db = new VaultDatabase(path);
try
{
    db.Initialize(password, new() { UiLanguage = "synthetic" }); db.Lock();
    if (!new FileInfo(path).Exists) throw new InvalidOperationException("Creation did not publish.");
    var cold = new VaultDatabase(path);
    try
    {
        if (!cold.Unlock(password) || cold.LoadConfiguration().UiLanguage != "synthetic")
            throw new InvalidOperationException("Cold owner failed.");
    }
    finally { cold.Lock(); }
    Console.WriteLine("PASS uninstrumented real creation and cold owner unlock");
    Console.WriteLine("RESULT total=4 passed=4 failed=0");
}
finally { db.Lock(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
