using System.Data.Common;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if(args.Length!=3)throw new ArgumentException("Expected application directory, empty temporary fixture root and result directory.");
var app=Path.GetFullPath(args[0]);
var root=Path.GetFullPath(args[1]);
var output=Path.GetFullPath(args[2]);
Directory.CreateDirectory(output);
var tempRoot=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))+Path.DirectorySeparatorChar;
if(!root.StartsWith(tempRoot,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)
   || !Directory.Exists(root) || Directory.EnumerateFileSystemEntries(root).Any()
   || (File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)
 throw new InvalidOperationException("Fixture root must be an empty owned directory beneath the temporary root.");
var reference=Mixed(24);
AssemblyLoadContext.Default.Resolving+=(context,name)=>{
 var path=Path.Combine(app,name.Name+".dll");
 return File.Exists(path)?context.LoadFromAssemblyPath(path):null;
};
var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(app,"LaunchFlow.dll"));
var vault=assembly.GetType("LaunchFlow.Services.VaultDatabase",true)!;
var configType=assembly.GetType("LaunchFlow.Models.Configuration",true)!;
var strength=assembly.GetType("LaunchFlow.Services.PasswordStrength",true)!;
var sqlite=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(app,"Microsoft.Data.Sqlite.dll")).GetType("Microsoft.Data.Sqlite.SqliteConnection",true)!;
var keyField=vault.GetField("_key",BindingFlags.Instance|BindingFlags.NonPublic)!;
object NewVault(string path)=>Activator.CreateInstance(vault,new object[]{path})!;
object? Call(object target,string method,params object?[] values)=>vault.GetMethod(method)!.Invoke(target,values);
bool Meets(string? value)=>(bool)strength.GetMethod("MeetsMinimum")!.Invoke(null,new object?[]{value})!;
bool Acceptable(string? value){var assessment=strength.GetMethod("Evaluate")!.Invoke(null,new object?[]{value})!;return(bool)assessment.GetType().GetProperty("Acceptable")!.GetValue(assessment)!;}
void Pools()=>sqlite.GetMethod("ClearAllPools",BindingFlags.Public|BindingFlags.Static)!.Invoke(null,null);
Exception RootException(Exception exception){for(var n=0;n<12&&exception.InnerException is not null;n++)exception=exception.InnerException;return exception;}
DbConnection Connection(string path){var b=new DbConnectionStringBuilder{["Data Source"]=path,["Mode"]="ReadOnly",["Pooling"]=false};var c=(DbConnection)Activator.CreateInstance(sqlite,new object[]{b.ConnectionString})!;c.Open();return c;}
long Scalar(DbConnection connection,string sql){using var cmd=connection.CreateCommand();cmd.CommandText=sql;return Convert.ToInt64(cmd.ExecuteScalar());}
byte[] Blob(DbConnection connection,string sql){using var cmd=connection.CreateCommand();cmd.CommandText=sql;return(byte[])cmd.ExecuteScalar()!;}
string Pick(string chars,int count)=>new(Enumerable.Range(0,count).Select(_=>chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
string Mixed(int length,string prefix="aZ7!",string? pool=null){var chars=(prefix+Pick(pool??"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*_-+=",length-prefix.Length)).ToCharArray();for(var n=chars.Length-1;n>0;n--){var other=RandomNumberGenerator.GetInt32(n+1);(chars[n],chars[other])=(chars[other],chars[n]);}return new string(chars);}
var corpus=new List<(string Label,string Group,string Value)>();
void Add(string group,int count,Func<string> value){for(var n=0;n<count;n++)corpus.Add(($"{group}-{n+1:D2}",group,value()));}
Add("mixed-12",20,()=>Mixed(12));
Add("mixed-16",20,()=>Mixed(16));
Add("mixed-24",16,()=>Mixed(24));
Add("alphanumeric-16",12,()=>Mixed(16,"aZ7","abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"));
Add("lowercase-passphrase",8,()=>string.Join(' ',Enumerable.Range(0,8).Select(_=>Pick("abcdefghjkmnpqrstuvwxyz",4))));
Add("digit-heavy-mixed",8,()=>Mixed(20,"aZ7!","0123456789"));
Add("letters-16",8,()=>Mixed(16,"aZ","abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ"));
Add("unicode-mixed",8,()=>"aZ7!"+Pick("界éЖλøΩβ中",16));
if(corpus.Count!=100||corpus.Select(x=>x.Value).Distinct().Count()!=100||corpus.Any(x=>x.Value==reference))throw new InvalidOperationException("Corpus construction failed");
corpus.Add(("reference-control-1","explicit-reference",reference));
corpus.Add(("reference-control-2","explicit-reference",reference));
var results=new List<Dictionary<string,object?>>();
var salts=new HashSet<string>(StringComparer.Ordinal);
var keys=new HashSet<string>(StringComparer.Ordinal);
var verifiers=new HashSet<string>(StringComparer.Ordinal);
var start=DateTimeOffset.UtcNow;
foreach(var item in corpus)
{
 var result=new Dictionary<string,object?>{{"case",item.Label},{"group",item.Group}};
 object? live=null,cold=null;
 byte[]? capturedKey=null;
 try
 {
  var directory=Path.Combine(root,item.Label);
  var path=Path.Combine(directory,"launchflow.db");
  live=NewVault(path);
  var noFileAfterConstructor=!File.Exists(path)&&!Directory.Exists(directory);
  var absent=Call(live,"InspectStorage")!.ToString()=="Absent";
  var noPasswordUnlock=!(bool)Call(live,"Unlock",reference)!;
  var noFileBeforeChoice=!File.Exists(path)&&!Directory.Exists(directory);
  var ui=Acceptable(item.Value);var storage=Meets(item.Value);
  result["ui_accepts"]=ui;result["storage_accepts"]=storage;
  if(!ui||!storage)throw new InvalidOperationException("Generated valid control rejected by admission");
  var configuration=Activator.CreateInstance(configType)!;
  foreach(var name in new[]{"StartWithWindows","RunOnUnlock","MinimizeToTray","SealBrowserProfilesOnLock"})configType.GetProperty(name)!.SetValue(configuration,true);
  configType.GetProperty("VaultLockMinutes")!.SetValue(configuration,10);
  Call(live,"Initialize",item.Value,configuration,null);
  var created=File.Exists(path);
  capturedKey=((byte[])keyField.GetValue(live)!).ToArray();
  var keyUnique=keys.Add(Convert.ToHexString(SHA256.HashData(capturedKey)));
  var state=Call(live,"InspectStorage")!.ToString();
  bool authLayout;
  using(var connection=Connection(path))
  {
   authLayout=Scalar(connection,"SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';")==3&&Scalar(connection,"SELECT count(*) FROM secure_meta WHERE id=1 AND length(vault_id)=16 AND length(salt)=16 AND length(verifier)>32 AND schema_version=3;")==1&&Scalar(connection,"SELECT count(*) FROM secure_meta;")==1&&Scalar(connection,"SELECT count(*) FROM secure_objects WHERE id='config' AND length(sealed)>32;")==1&&Scalar(connection,"SELECT count(*) FROM secure_objects;")==1&&Scalar(connection,"SELECT count(*) FROM secure_credentials;")==0;
   result["unique_salt"]=salts.Add(Convert.ToHexString(Blob(connection,"SELECT salt FROM secure_meta WHERE id=1;")));
   var verifier=Blob(connection,"SELECT verifier FROM secure_meta WHERE id=1;");
   result["unique_encrypted_verifier"]=verifiers.Add(Convert.ToHexString(SHA256.HashData(verifier)));
  }
  Call(live,"Lock");
  var locked=keyField.GetValue(live)is null;
  cold=NewVault(path);
  var foreign=Mixed(24);
  while(foreign==item.Value||foreign==reference)foreign=Mixed(24);
  var foreignRejected=!(bool)Call(cold,"Unlock",foreign)!&&keyField.GetValue(cold)is null;
  var referenceRejected=item.Group=="explicit-reference"||(!(bool)Call(cold,"Unlock",reference)!&&keyField.GetValue(cold)is null);
  var ownerAccepted=(bool)Call(cold,"Unlock",item.Value)!;
  Call(cold,"Lock");Pools();
  var needles=new[]{Encoding.UTF8.GetBytes(item.Value),Encoding.Unicode.GetBytes(item.Value),SHA256.HashData(Encoding.UTF8.GetBytes(item.Value)),capturedKey};
  var materialFound=false;
  foreach(var file in Directory.EnumerateFiles(directory,"launchflow.db*"))
  {
   var bytes=File.ReadAllBytes(file);
   materialFound|=needles.Any(needle=>bytes.AsSpan().IndexOf(needle)>=0);
   CryptographicOperations.ZeroMemory(bytes);
  }
  foreach(var needle in needles)CryptographicOperations.ZeroMemory(needle);
  result["absent_before_creation"]=noFileAfterConstructor&&absent&&noPasswordUnlock&&noFileBeforeChoice;
  result["database_created"]=created;
  result["authenticated_layout"]=authLayout&&state=="PresentNeedsAuthentication";
  result["unique_derived_key"]=keyUnique;
  result["key_revoked_on_lock"]=locked;
  result["foreign_password_rejected"]=foreignRejected;
  result["reference_not_a_universal_password"]=referenceRejected;
  result["owner_cold_unlock"]=ownerAccepted;
  result["plaintext_password_raw_sha256_or_master_key_found"]=materialFound;
  result["passed"]=result.Where(x=>x.Value is bool&&x.Key!="plaintext_password_raw_sha256_or_master_key_found").All(x=>(bool)x.Value!)&&!materialFound;
 }
 catch(Exception exception){result["passed"]=false;result["exception_type"]=RootException(exception).GetType().Name;}
 finally
 {
  if(capturedKey is not null)CryptographicOperations.ZeroMemory(capturedKey);
  if(live is not null)try{Call(live,"Lock");}catch{}
  if(cold is not null)try{Call(cold,"Lock");}catch{}
  Pools();
 }
 results.Add(result);
 if(results.Count%10==0||results.Count==corpus.Count)
 {
  var progress=new{completed=results.Count,expected=corpus.Count,passed=results.Count(x=>x.TryGetValue("passed",out var passed)&&passed is true),elapsed_seconds=(DateTimeOffset.UtcNow-start).TotalSeconds};
  File.WriteAllText(Path.Combine(output,"progress.json"),JsonSerializer.Serialize(progress));
  File.WriteAllText(Path.Combine(output,"cases-so-far.json"),JsonSerializer.Serialize(results));
  Console.WriteLine(JsonSerializer.Serialize(progress));
 }
}
var rejectedCases=new List<(string Label,string? Value)>{("null",null),("empty",""),("short-mixed-4","Ax1!"),("short-mixed-10",Mixed(10)),("short-mixed-11",Mixed(11)),("lowercase-only-12",Pick("abcdefghjkmnpqrstuvwxyz",12)),("common-phrase","password123456789!")};
for(var n=1;n<=8;n++)rejectedCases.Add(($"numeric-only-{n:D2}",Pick("0123456789",20)));
var refusals=new List<object>();
foreach(var item in rejectedCases)
{
 var path=Path.Combine(root,"rejection-"+item.Label,"launchflow.db");var db=NewVault(path);string? type=null;
 try{Call(db,"Initialize",item.Value,Activator.CreateInstance(configType),null);}catch(Exception exception){type=RootException(exception).GetType().Name;}
 var ui=Acceptable(item.Value);var storage=Meets(item.Value);var passed=!ui&&!storage&&type=="ArgumentException"&&!File.Exists(path)&&!(bool)Call(db,"Unlock",reference)!&&!File.Exists(path);
 refusals.Add(new{label=item.Label,ui_accepts=ui,storage_accepts=storage,exception_type=type,no_database_created=!File.Exists(path),passed});
}
var summary=new{assembly_sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(app,"LaunchFlow.dll")))),assembly=assembly.GetName().FullName,mvid=assembly.ManifestModule.ModuleVersionId,runtime=RuntimeInformation.FrameworkDescription,scope="Actual complete production application DLL methods; not native UI or resolution of the VMware incident. Bind source separately to this assembly digest.",random_creation_cases=100,reference_creation_cases=2,total_creation_cases=results.Count,creation_passed=results.Count(x=>x.TryGetValue("passed",out var passed)&&passed is true),unique_salts=salts.Count,unique_derived_keys=keys.Count,unique_encrypted_verifiers=verifiers.Count,elapsed_seconds=(DateTimeOffset.UtcNow-start).TotalSeconds,results,refusal_controls=refusals};
File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(summary,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(new{completed=results.Count,passed=summary.creation_passed,unique_salts=salts.Count,unique_derived_keys=keys.Count,runtime=summary.runtime}));
return summary.creation_passed==102&&salts.Count==102&&keys.Count==102&&verifiers.Count==102&&refusals.Count==15&&refusals.All(x=>(bool)x.GetType().GetProperty("passed")!.GetValue(x)!)?0:1;
