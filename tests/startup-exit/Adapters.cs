// Native UI, storage and engine adapters only; startup and installer request bodies are extracted unchanged.
namespace System.Windows {
 public enum ShutdownMode {OnExplicitShutdown,OnMainWindowClose}
 public enum WindowState {Normal,Minimized}
 public enum MessageBoxButton {OK,YesNo}
 public enum MessageBoxImage {Error,Warning,Information,Question}
 public enum MessageBoxResult {OK,Yes,No}
 public class Window {
  public static Action<Window>? OnDialog;
  public static int DialogsShown;
  public bool Closed; public bool IsLoaded; public bool IsVisible; public bool IsEnabled=true; public WindowState WindowState; public bool ShowInTaskbar;
  public Window(){if(LaunchFlow.App.Current is LaunchFlow.App app){app.Windows.Add(this);app.WindowHistory.Add(this);}}
  public virtual bool? ShowDialog(){DialogsShown++;OnDialog?.Invoke(this);return !Closed;}
  public virtual void Close(){Closed=true;IsLoaded=false;IsVisible=false;LaunchFlow.App.Current?.Windows.Remove(this);}
  public void Show(){IsLoaded=true;IsVisible=true;} public void Hide(){IsVisible=false;}
 }
 public class Application {public static LaunchFlow.App Current=>LaunchFlow.App.Current!;}
 public static class MessageBox {
  public static Action? OnShow; public static MessageBoxResult Result=MessageBoxResult.Yes;
  public static MessageBoxResult Show(string a,string b,MessageBoxButton c,MessageBoxImage d){OnShow?.Invoke();return Result;}
  public static MessageBoxResult Show(Window owner,string a,string b,MessageBoxButton c,MessageBoxImage d)=>Show(a,b,c,d);
 }
}
namespace Forms {public enum ToolTipIcon {Warning}}
namespace Microsoft.Win32 {
 public class OpenFileDialog {
  public string Title="",Filter="",InitialDirectory="",FileName="fixture";
  public static Action? OnConstruct,OnShow;
  public OpenFileDialog(){OnConstruct?.Invoke();}
  public bool? ShowDialog(){OnShow?.Invoke();return true;}
 }
}
namespace LaunchFlow.Models {
 public class SiteCredential {}
 public class Workflow {public bool Enabled;}
 public class Configuration {
  public bool StartWithWindows,RunOnUnlock,RunOnAppStart,MinimizeToTray,SealBrowserProfilesOnLock,PreferTaskSchedulerAutostart;
  public int VaultLockMinutes;public string GlobalShortcut="";public List<Workflow> Workflows=[];
 }
}
namespace LaunchFlow {
 using System.Windows; using LaunchFlow.Services;using LaunchFlow.Models;
 public partial class App {
  public static App? Current;public object? MainWindow;public List<Window> Windows=[];public List<Window> WindowHistory=[];
  public int Shutdowns;private string? _pendingOpenPath;private bool _restorePending=false;public bool RestorePendingForTest=>_restorePending;
  public ShutdownMode ShutdownMode;public App(){Current=this;}
  private static readonly string ActivationPipeName="LaunchFlow.StartupExit.Fixture."+Guid.NewGuid().ToString("N");
  private Dispatch Dispatcher=new();public Task ListenForTest(CancellationToken token)=>ListenForActivationAsync(token);
  public static string PipeForTest=>ActivationPipeName;
  private void HandleExternalOpen(string path){}private void RestoreRunningWindow(){}
  class Dispatch {public object BeginInvoke(Action callback){callback();return new object();}}
  public void Shutdown(){Shutdowns++;if(VaultDatabase.Current.InFlight)throw new Exception("Shutdown interrupted a storage operation");VaultDatabase.Current.Lock();}
  internal static bool TryCreateAndVerifyVault(VaultDatabase db,string p,Configuration c,Dictionary<Guid,SiteCredential>? s=null){db.Initialize(p,c,s);return true;}
 }
 public partial class MainWindow : Window {
  public static int Constructed;public static Action? OnConstruct;
  public MainWindow(){Constructed++;OnConstruct?.Invoke();}
  public void QueueOpenFile(string p){}
  public bool PendingDialog,Dirty,SaveSucceeds=true,CleanupSucceeds=true;
  private bool HasPendingEditorDialog=>PendingDialog;
  private bool _installerShutdownInProgress,_exiting=false,_installerProfileSealFailed;
  private CancellationTokenSource? _unlockCancellation=new();
  private RunUi _runUi=new();private Editor _editorSession=new();private Drain _exitDrain=new();
  public int Saves,Drains,Notifications;public Task? CleanupWait;
  public void SetDirty(bool dirty){_editorSession.IsDirty=dirty;}
  private bool SaveAll(){Saves++;if(SaveSucceeds)_editorSession.IsDirty=false;return SaveSucceeds;}
  private async Task CompleteOwnedExitAsync(bool installer){Drains++;_exiting=true;try{if(CleanupWait is not null)await CleanupWait;if(_installerProfileSealFailed || !CleanupSucceeds)throw new Exception("fixture cleanup refusal");_exitDrain.Permitted=true;Close();}catch{_exiting=false;throw;}}
  public Task ResetForTest()=>ContinueToSetupAfterVaultResetAsync();
  public bool ExitingForTest=>_exiting;
  public bool PermitForTest=>_exitDrain.Permitted;
  private Hello _helloUnlock=new();private Configuration _configuration=new();
  private bool _vaultCleanupFailed=false,_vaultLockInProgress=false;
  private Task? _finalRunnerDisposeTask;private Runner _runner=new();
  private Task SecureVaultLockAsync(string message,bool sealProfiles=true){VaultDatabase.Current.Lock();return Task.CompletedTask;}
  class Hello {public void InvalidateOutstandingEnables(){}}
  class Runner {public ValueTask DisposeAsync()=>ValueTask.CompletedTask;}
  private void UpdateRunButtons(){}private static void AppendLog(string s){}
  private void NotifyTray(string a,string b,Forms.ToolTipIcon c){Notifications++;}
  class RunUi {public CancellationTokenSource? Cancellation=new();}
  class Editor {public bool IsDirty;public int Revision=>0;public bool TryAcknowledge(int revision){IsDirty=false;return true;}}
  class Drain {public bool Permitted;public async Task PrepareAsync(Func<Task> cleanup){await cleanup();Permitted=true;}}
 }
 public class VaultFreshStartWindow:Window {}
 public class SetupWindow:Window {public string ChosenPassword=>"synthetic";public string TakeChosenPassword()=>ChosenPassword;public bool StartWithWindows,RunAfterUnlock,RunOnAppStart,RunInTray;}
 public class UnlockWindow:Window {public bool ResetRequested;public string Password=>"synthetic";public bool UsedWindowsHello;}
 public class SecretPromptWindow:Window {public SecretPromptWindow(string title,string text){_ = title;_ = text;}public bool RequireStrongPassword;public string Secret=>"synthetic";}
 public class VaultRecoveryWindow:Window {public enum RecoveryChoice{Cancel,StartFresh,RestoreSqlite,Portable}public RecoveryChoice Choice=RecoveryChoice.StartFresh;}
}
namespace LaunchFlow.Services {
 using LaunchFlow.Models;
 public enum VaultStorageState {Absent,PresentNeedsAuthentication,IncompleteOrUnsupported,LegacyUpgradeRequired}
 public class VaultDatabase {
  public static VaultDatabase Current=new(); public VaultStorageState State;public bool IsUnlocked,InFlight;public bool MissingMain,RetainedStage;public bool Exists=>!MissingMain && State!=VaultStorageState.Absent;
  public string DatabasePath=>"fixture";public string DataDirectory=>"fixture";public int Creates,Unlocks,Quarantines,Resets,Locks;public byte[] Committed=[1,2,3];
  public static Action? OnCreate;public static Action? OnInspect;
  public bool HasRetainedCreationStage()=>RetainedStage;
  public VaultStorageState InspectStorage(){OnInspect?.Invoke();return State;}
  public void Initialize(string p,Configuration c,Dictionary<Guid,SiteCredential>? s=null){Creates++;InFlight=true;try{Committed=[4,5,6];OnCreate?.Invoke();IsUnlocked=true;}finally{InFlight=false;}}
  public bool Unlock(string p){Unlocks++;IsUnlocked=true;return true;}
  public void Lock(){Locks++;IsUnlocked=false;}
  public string UpgradeLegacyVault(string s){Creates++;IsUnlocked=true;return "fixture";}
 }
 public static class StartupDiagnostics {
  public enum Stage {DependencyProbeStarted,DependencyProbeRejected,VaultUnlockStarted,VaultUnlockCancelled,VaultReady,WindowConstructionStarted,WindowShown,StartupException,VaultRecoveryFailed,VaultRecoveryStarted,SetupHandoff}
  public enum Admission {HandoffOrdinalEqual}
  public static void Record(Stage s,Exception? e=null){} public static void RecordAdmission(Admission a,bool b){}
 }
 public static class WindowsApplicationControl {public static (bool Ready,string Message) CheckRequiredComponents()=>(true,"");}
 public static class VaultRecoveryService {
  public static bool CanStartFreshVault(string s)=>true;
  public static void QuarantineLiveVault(string a,string b){VaultDatabase.Current.Quarantines++;VaultDatabase.Current.State=VaultStorageState.Absent;}
  public static bool HasIncompletePreservation(string s)=>false;public static string RecoveryDirectory=>"fixture";
  public static IEnumerable<string> ListSqliteRecoveryCopies()=>["fixture"];
  public static string RestoreSqliteBackup(string s){VaultDatabase.Current.Quarantines++;return "fixture";}
  public static string QuarantineLiveVault(){VaultDatabase.Current.Quarantines++;return "fixture";}
 }
 public static class LegacyMigration {
  public static bool HasLegacy=>false;public static bool VerifyOldMaster(string s)=>true;
  public static (Configuration Config,Dictionary<Guid,SiteCredential> Secrets) ReadLegacyData()=>(new(),[]);
 }
 public static class ProfileVaultService {public static int Unseals;public static void UnsealAll(){Unseals++;}public static void SealAll(){}}
 public static class WindowsHelloService {public static void MarkPasswordVerified(){}public static bool IsEnabled=>false;public static void DisableStrict(){}}
 public static class VaultResetService {public static void ResetLocalVault(bool wipeProfiles){VaultDatabase.Current.Resets++;VaultDatabase.Current.State=VaultStorageState.Absent;}}
 public static class ExitCleanupWait {public static Task RequireAsync(Task operation,TimeSpan timeout,string label)=>operation;}
 public static class CrashLog {public static void Write(string s,Exception e){}}
 public static class PasswordStrength {public const int MinimumLength=12;public static bool MeetsMinimum(string s)=>true;public static string RejectionNotice((string Hint,int Score) result)=>result.Hint;public static (string Hint,int Score) Evaluate(string s)=>("fixture",4);}
 public static class RunIssueNotice {public static string SafeExceptionReason(Exception e)=>"fixture";}
 public static class TaskSchedulerService {public static void Apply(bool s){}}
 public static class StartupService {public static int Applies;public static void Apply(bool s){Applies++;}}
 public class StartupRegistrationService(Action<bool> first,Action<bool> second){public void Apply(bool a,bool b){if(b)first(a);else second(a);}}
 public static class ImportConfigurationProjection {public static Configuration ForThisDevice(Configuration a,Configuration b,string c)=>a;}
 public static class PortableExport {public class PortablePackage {public Configuration Config=new();public Dictionary<Guid,SiteCredential> Credentials=[];}}
 public static class SecureTransferService {public static PortableExport.PortablePackage Read(string a,string b)=>new();}
}
