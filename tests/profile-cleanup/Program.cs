using System.IO.Compression;
using System.Security.Cryptography;
using LaunchFlow.Services;

if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Linux permission fault fixture; native Windows gate is separate.");
var root=Path.Combine(Directory.GetCurrentDirectory(),"fixture-"+Guid.NewGuid().ToString("N"));
VaultDatabase.Current.DataDirectory=root;
try
{
    var id="11111111111111111111111111111111";
    var profile=Path.Combine(ProfileVaultService.ProfilesRoot,id);
    var child=Path.Combine(profile,"locked");Directory.CreateDirectory(child);
    File.WriteAllText(Path.Combine(profile,"deleted.txt"),"must survive deletion failure and retry");
    File.WriteAllText(Path.Combine(child,"retained.txt"),"permission fault child");
    File.SetUnixFileMode(child,UnixFileMode.UserRead|UnixFileMode.UserExecute);
    AggregateException? error=null;try{ProfileVaultService.SealAll();}catch(AggregateException ex){error=ex;}
    Check(error!=null,"actual recursive-delete fault is reported");
    var archive=Path.Combine(ProfileVaultService.SealedRoot,id+".profile.aes");var original=File.ReadAllBytes(archive);
    Check(Entries(original).Order().SequenceEqual(new[]{"deleted.txt","locked/retained.txt"}),"published encrypted snapshot is complete before cleanup failure");
    Check(!Directory.Exists(profile),"partially deleted source is never left in the authoritative managed path");
    Check(Directory.EnumerateDirectories(ProfileVaultService.ProfilesRoot,".sealed-*").Count()==1,"failed cleanup remains under reserved disposal ownership");
    ProfileVaultService.SealAll();
    Check(File.ReadAllBytes(archive).SequenceEqual(original),"ordinary retry cannot overwrite the complete ciphertext from partial cleanup");
    ProfileVaultService.UnsealAll();
    Check(File.ReadAllText(Path.Combine(profile,"deleted.txt"))=="must survive deletion failure and retry" && File.ReadAllText(Path.Combine(profile,"locked/retained.txt"))=="permission fault child","restore recovers both files despite retained disposal debris");
    Check(File.ReadAllBytes(archive).SequenceEqual(original),"restore retains exact original ciphertext");
    Check(!Directory.EnumerateFiles(ProfileVaultService.SealedRoot,".sealed-*").Any(),"disposal debris never becomes a sealed profile");
    var reserved=Path.Combine(ProfileVaultService.ProfilesRoot,".restore-22222222222222222222222222222222");Directory.CreateDirectory(reserved);File.WriteAllText(Path.Combine(reserved,"partial.txt"),"synthetic crash residue");
    ProfileVaultService.SealAll();
    Check(Directory.Exists(reserved)&&!File.Exists(Path.Combine(ProfileVaultService.SealedRoot,Path.GetFileName(reserved)+".profile.aes")),"reserved restore debris remains quarantined, never reclassified");
    var helperArchive=Path.Combine(ProfileVaultService.SealedRoot,".restore-33333333333333333333333333333333.profile.aes");File.WriteAllBytes(helperArchive,original);
    ProfileVaultService.UnsealAll();
    Check(!Directory.Exists(Path.Combine(ProfileVaultService.ProfilesRoot,".restore-33333333333333333333333333333333"))&&File.ReadAllBytes(helperArchive).SequenceEqual(original),"old helper ciphertext is preserved without publishing a reserved profile");
}
finally
{
    if(Directory.Exists(root))
    {
        foreach(var dir in Directory.GetDirectories(root,"*",SearchOption.AllDirectories))File.SetUnixFileMode(dir,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        Directory.Delete(root,true);
    }
}

static string[] Entries(byte[] bytes)
{
    var key=ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(VaultDatabase.Current.DataDirectory,"profile-key.dpapi")),null,DataProtectionScope.CurrentUser);
    var zip=new byte[bytes.Length-28];
    try{using var aes=new AesGcm(key,16);aes.Decrypt(bytes.AsSpan(0,12),bytes.AsSpan(12,zip.Length),bytes.AsSpan(bytes.Length-16),zip);using var archive=new ZipArchive(new MemoryStream(zip),ZipArchiveMode.Read);return archive.Entries.Select(e=>e.FullName).ToArray();}
    finally{CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(zip);}
}
static void Check(bool ok,string name){if(!ok)throw new Exception("FAIL "+name);Console.WriteLine("PASS "+name);}
