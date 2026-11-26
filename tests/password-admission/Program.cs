using LaunchFlow;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Data.Sqlite;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Synthetic inputs exist in memory only. Output contains fixed IDs/booleans only.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LAUNCHFLOW_FIXTURE_DATA_ROOT")) ||
    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LAUNCHFLOW_TEST_VAULT_PATH")))
    throw new InvalidOperationException("Run this fixture through tests/password-admission/run.py.");
var root = Path.Combine(Path.GetTempPath(), "fixtures-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var results = new List<object>();
int failed = 0;
void Check(string id, Func<bool> body)
{
    try { bool ok = body(); results.Add(new { id, passed = ok }); if (!ok) failed++; Console.WriteLine((ok ? "PASS " : "FAIL ") + id); }
    catch (Exception ex) { failed++; results.Add(new { id, passed = false, exceptionType = ex.GetType().Name }); Console.WriteLine("ERROR " + id + " " + ex.GetType().Name); }
}
string GeneratedOwner() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
string Digits() => string.Concat(Enumerable.Range(0, 16).Select(i => (char)(49 + i % 8)));
string Alternating(int count) => string.Concat(Enumerable.Range(0, count).Select(i => i % 2 == 0 ? (char)97 : (char)66));
string CommonDecorated() => new string((char)32, 2) + string.Concat(new[]{119,101,108,99,111,109,101}.Select(i => (char)i)) + new string((char)49, 8);
string CommonSupplementary() => string.Concat(new[]{119,101,108,99,111,109,101}.Select(i => (char)i)) + string.Concat(Enumerable.Range(0,8).Select(i => char.ConvertFromUtf32(0x1D7CE+i)));
string AlternatingSupplementary() => string.Concat(Enumerable.Range(0,13).Select(i => char.ConvertFromUtf32(0x20000+i%2)));
object Static(string name, params object[] args) => typeof(VaultDatabase).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!;
void Legacy(string path, string password)
{
    var salt = RandomNumberGenerator.GetBytes(16);
    var key = (byte[])Static("DeriveKey", password, salt, 16384, 1, 1);
    try
    {
        byte[] Seal(byte[] data) => (byte[])Static("Encrypt", data, key);
        using var c = new SqliteConnection("Data Source=" + path); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE vault_meta(id INTEGER PRIMARY KEY,salt BLOB,verifier BLOB,argon_memory_kb INTEGER,argon_iterations INTEGER,argon_lanes INTEGER,schema_version INTEGER); CREATE TABLE objects(id TEXT PRIMARY KEY,sealed BLOB); CREATE TABLE credentials(workflow_id TEXT PRIMARY KEY,sealed BLOB); INSERT INTO vault_meta VALUES(1,$s,$v,16384,1,1,2); INSERT INTO objects VALUES('config',$c);";
        cmd.Parameters.AddWithValue("$s", salt); cmd.Parameters.AddWithValue("$v", Seal(Encoding.UTF8.GetBytes("LaunchFlow.Vault.v1")));
        cmd.Parameters.AddWithValue("$c", Seal(JsonSerializer.SerializeToUtf8Bytes(new Configuration())));
        cmd.ExecuteNonQuery();
    }
    finally { CryptographicOperations.ZeroMemory(key); }
}
try
{
    Check("POL-01", () => !PasswordStrength.Evaluate(Digits()).Acceptable);
    Check("POL-02", () => !PasswordStrength.Evaluate(Digits() + (char)32).Acceptable);
    Check("POL-03", () => !PasswordStrength.Evaluate(new string((char)32, 14) + new string((char)97, 6)).Acceptable);
    Check("POL-04", () => !PasswordStrength.Evaluate(Alternating(16)).Acceptable);
    Check("POL-05", () => !PasswordStrength.Evaluate(Alternating(17)).Acceptable);
    Check("POL-06", () => !PasswordStrength.Evaluate(CommonDecorated()).Acceptable);
    Check("POL-07", () => !PasswordStrength.Evaluate(CommonSupplementary()).Acceptable);
    Check("POL-08", () => !PasswordStrength.Evaluate(AlternatingSupplementary()).Acceptable);
    var owner = GeneratedOwner();
    Check("CTL-01", () => PasswordStrength.Evaluate(owner).Acceptable);
    Check("CTL-02", () => PasswordStrength.Evaluate(string.Join((char)32, Enumerable.Range(0,4).Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(5))))).Acceptable);
    Check("CTL-03", () => PasswordStrength.Evaluate(string.Concat(Enumerable.Range(0,24).Select(i => char.ConvertFromUtf32(0x4e00 + i)))).Acceptable);
    Check("UI-01", () => { var w = new SetupWindow(); w.PasswordBox.Password = w.ConfirmBox.Password = Digits() + (char)32; w.SubmitForTest(); return w.DialogResult != true; });
    Check("UI-02", () => { var w = new SecretPromptWindow("probe", "probe") { RequireStrongPassword = true }; w.SecretBox.Password = CommonDecorated(); w.SubmitForTest(); return w.DialogResult != true; });
    Check("UI-03", () => { var w = new SecretPromptWindow("probe", "probe"); var p = Digits(); w.SecretBox.Password = p; w.SubmitForTest(); return w.DialogResult == true && string.Equals(p,w.Secret,StringComparison.Ordinal); });
    Check("UI-04", () => { var w = new SecretPromptWindow("probe", "probe") { RequireStrongPassword = true }; w.SecretBox.Password = Digits(); w.SubmitForTest(); w.CancelForTest(); return w.DialogResult == false && w.Secret == "" && w.SecretBox.Password == ""; });
    Check("DB-01", () => { var p = Path.Combine(root,"db01.db"); var v = new VaultDatabase(p); bool refused=false; try { v.Initialize(Digits() + (char)32,new Configuration()); } catch(ArgumentException) { refused=true; } finally { v.Lock(); } return refused && !File.Exists(p); });
    Check("DB-02", () => { var p = Path.Combine(root,"db02.db"); var v = new VaultDatabase(p); v.Initialize(owner,new Configuration()); bool refused=false; try { v.ChangeMasterPassword(owner, Alternating(17)); } catch(ArgumentException) { refused=true; } finally { v.Lock(); } var cold = new VaultDatabase(p); try { return refused && cold.Unlock(owner); } finally { cold.Lock(); } });
    Check("DB-03", () => { var p = Path.Combine(root,"db03.db"); var v = new VaultDatabase(p); v.Initialize(owner,new Configuration()); bool refused=false; try { v.ChangeMasterPassword(owner, AlternatingSupplementary()); } catch(ArgumentException) { refused=true; } finally { v.Lock(); } var cold = new VaultDatabase(p); try { return refused && cold.Unlock(owner); } finally { cold.Lock(); } });
    Check("EXP-01", () => { bool refused=false; var p = Path.Combine(root,"exp01.launchflow"); try { PortableExport.Write(p,CommonDecorated(),new()); } catch(ArgumentException) { refused=true; } return refused && !File.Exists(p); });
    Check("EXP-02", () => { bool refused=false; var p = Path.Combine(root,"exp02.launchflow"); try { PortableExport.Write(p,CommonSupplementary(),new()); } catch(ArgumentException) { refused=true; } return refused && !File.Exists(p); });
    Check("CTL-04", () => { var p = Path.Combine(root,"ctl04.db"); var v = new VaultDatabase(p); v.Initialize(owner,new Configuration()); v.Lock(); var cold = new VaultDatabase(p); try { return cold.Unlock(owner) && !cold.ConfirmPassword(GeneratedOwner()); } finally { cold.Lock(); } });
    Check("LEG-01", () => { var p = Path.Combine(root,"leg01.db"); var pw=Digits(); Legacy(p,pw); var v = new VaultDatabase(p); if (PasswordStrength.Evaluate(pw).Acceptable || v.InspectStorage()!=VaultStorageState.LegacyUpgradeRequired) return false; v.UpgradeLegacyVault(pw); v.Lock(); var cold=new VaultDatabase(p); try { return cold.Unlock(pw); } finally { cold.Lock(); } });
    Check("LEG-02", () => { var p = Path.Combine(root,"leg02.launchflow"); var pw=Digits();
        // Model an old archive using unchanged wire format and actual Argon2 implementation.
        var salt=RandomNumberGenerator.GetBytes(16); var nonce=RandomNumberGenerator.GetBytes(12); var payload=JsonSerializer.SerializeToUtf8Bytes(new PortableExport.PortablePackage()); var cipher=new byte[payload.Length]; var tag=new byte[16];
        using var argon=new Konscious.Security.Cryptography.Argon2id(Encoding.UTF8.GetBytes(pw)){Salt=salt,DegreeOfParallelism=2,MemorySize=65536,Iterations=3}; var key=argon.GetBytes(32);
        try { using(var aes=new AesGcm(key,16)) aes.Encrypt(nonce,payload,cipher,tag); using(var b=new BinaryWriter(File.Create(p))) { b.Write(Encoding.ASCII.GetBytes("LFLW1")); b.Write(salt); b.Write(nonce); b.Write(tag); b.Write(cipher.Length); b.Write(cipher); } return SecureTransferService.Read(p,pw).Credentials.Count==0; } finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(payload); }
    });
    Check("CTL-05", () => { var p=Path.Combine(root,"ctl05.db"); var pw=(char)32 + owner + (char)32; var v=new VaultDatabase(p); v.Initialize(pw,new()); v.Lock(); var cold=new VaultDatabase(p); try { return cold.Unlock(pw) && !cold.ConfirmPassword(pw.Trim()); } finally { cold.Lock(); } });
    Check("STO-01", () => { var p=Path.Combine(root,"sto01.db"); var v=new VaultDatabase(p); var original=new Configuration(); v.Initialize(owner,original); var session=v.SessionGeneration; using(var c=new SqliteConnection("Data Source="+p)){ c.Open(); using var cmd=c.CreateCommand(); cmd.CommandText="CREATE TRIGGER refuse_update BEFORE INSERT ON secure_objects BEGIN SELECT RAISE(ABORT,'fixture-refusal'); END;"; cmd.ExecuteNonQuery(); } var changed=v.LoadConfiguration(); changed.RunOnAppStart=!original.RunOnAppStart; bool refused=false; try { new ConfigStore(v).SaveDurable(changed,session); } catch(SqliteException){refused=true;} var liveUnchanged=v.LoadConfiguration().RunOnAppStart==original.RunOnAppStart; v.Lock(); var cold=new VaultDatabase(p); try { return refused && liveUnchanged && cold.Unlock(owner) && cold.LoadConfiguration().RunOnAppStart==original.RunOnAppStart; } finally { cold.Lock(); } });
    Check("STO-02", () => { var p=Path.Combine(root,"sto02.db"); var v=new VaultDatabase(p); bool refused=false; try { v.Initialize(owner,new(),new(){ [Guid.Empty]=new("fixture",GeneratedOwner()) }); } catch(InvalidDataException){refused=true;} finally {v.Lock();} return refused && !v.Exists && !v.HasRetainedCreationStage() && v.InspectStorage()==VaultStorageState.Absent; });
    Check("REC-03", () => { var p=Path.Combine(root,"rec03.db"); var v=new VaultDatabase(p); v.Initialize(owner,new()); v.Lock(); var before=File.ReadAllBytes(p); bool refused=false; try { VaultRecoveryService.QuarantineLiveVault(p,root); } catch(InvalidOperationException){refused=true;} return refused && before.SequenceEqual(File.ReadAllBytes(p)); });
    Check("REC-01", () => { var p=Path.Combine(root,"rec01.db"); var stage=Path.Combine(root,".rec01.db.create-owned"); Directory.CreateDirectory(stage); File.WriteAllBytes(Path.Combine(stage,"candidate.db"),new byte[]{1}); var v=new VaultDatabase(p); bool refused=false; try { v.Initialize(owner,new()); } catch(InvalidOperationException){ refused=true; } return refused && v.HasRetainedCreationStage() && v.InspectStorage()==VaultStorageState.IncompleteOrUnsupported && !v.Exists && File.Exists(Path.Combine(stage,"candidate.db")); });
    Check("REC-02", () => { var p=Path.Combine(root,"rec02.db"); File.WriteAllBytes(p,new byte[]{1,2,3}); var q=VaultRecoveryService.QuarantineLiveVault(p,root); return !File.Exists(p) && File.ReadAllBytes(q).SequenceEqual(new byte[]{1,2,3}) && !VaultRecoveryService.HasIncompletePreservation(root); });
}
finally { SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"results.json"),JsonSerializer.Serialize(new { failed, cases=results },new JsonSerializerOptions{WriteIndented=true}));
return failed == 0 ? 0 : 1;
