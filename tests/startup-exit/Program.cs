using LaunchFlow;
using LaunchFlow.Services;
using System.Windows;
var failures=0;
void Check(bool value,string name){Console.WriteLine($"{(value?"PASS":"FAIL")} {name}");if(!value)failures++;}
App Fresh(VaultStorageState state=VaultStorageState.Absent){Window.OnDialog=null;MessageBox.OnShow=null;MainWindow.OnConstruct=null;MainWindow.Constructed=0;VaultDatabase.OnCreate=null;VaultDatabase.OnInspect=null;VaultDatabase.Current=new(){State=state};ProfileVaultService.Unseals=0;StartupService.Applies=0;return new App();}
foreach(var loaded in new[]{true,false}) {
 var app=Fresh();var main=new MainWindow{IsLoaded=loaded};app.MainWindow=main;
 app.ExitForTest();Check(main.Drains==1 && main.Closed && app.Shutdowns==0,loaded?"loaded_main_delegates":"unloaded_main_preserves_owned_cleanup");
}
{var app=Fresh();app.ExitForTest();Check(app.Shutdowns==1,"no_window_requests_shutdown");}
foreach(var state in new[]{VaultStorageState.Absent,VaultStorageState.PresentNeedsAuthentication,VaultStorageState.IncompleteOrUnsupported}) {
 var app=Fresh(state);var db=VaultDatabase.Current;var before=db.Committed.ToArray();bool entered=false;
 Window.OnDialog=w=>{entered=true;app.ExitForTest();};
 app.StartupForTest();
 Check(entered && app.Shutdowns>0 && MainWindow.Constructed==0 && db.Creates==0 && db.Unlocks==0 && db.Quarantines==0 && db.Resets==0 && db.Committed.SequenceEqual(before),$"{state}_startup_exit_preserves_and_never_constructs_main");
}
foreach(var loaded in new[]{true,false})
foreach(var kind in new[]{"pending_editor","locked_dirty","save_refused","cleanup_refused","saved_cleanup"}) {
 var app=Fresh();var main=new MainWindow{IsLoaded=loaded};app.MainWindow=main;var db=VaultDatabase.Current;db.IsUnlocked=kind!="locked_dirty";
 main.PendingDialog=kind=="pending_editor";main.SetDirty(kind is "locked_dirty" or "save_refused" or "saved_cleanup");main.SaveSucceeds=kind!="save_refused";main.CleanupSucceeds=kind!="cleanup_refused";
 app.ExitForTest();
 Check(app.Shutdowns==0 && (kind=="saved_cleanup"? main.Closed && main.Saves==1 && main.Drains==1:!main.Closed && main.Notifications==1),$"{(loaded?"loaded":"unloaded")}_main_{kind}_contract");
 if(kind=="cleanup_refused"){main.CleanupSucceeds=true;app.ExitForTest();Check(main.Closed && main.Drains==2,$"{(loaded?"loaded":"unloaded")}_main_refusal_allows_retry");}
}
{
 var app=Fresh();var main=new MainWindow{IsLoaded=true};app.MainWindow=main;var gate=new TaskCompletionSource();main.CleanupWait=gate.Task;
 app.ExitForTest();app.ExitForTest();Check(main.Drains==1 && !main.Closed,"loaded_main_reentrant_cleanup_is_joined");gate.SetResult();await Task.Delay(10);Check(main.Closed && main.Drains==1,"loaded_main_delayed_cleanup_completes");
}
{
 var app=Fresh();app.ExitForTest();app.ExitForTest();app.ContinueVaultResetSetup();app.StartupForTest();
 Check(app.Shutdowns==1 && MainWindow.Constructed==0 && VaultDatabase.Current.Creates==0,"repeated_exit_and_reset_continuation_never_restart");
}
{
 var app=Fresh();bool reentered=false;Window.OnDialog=w=>{if(reentered)return;reentered=true;app.ContinueVaultResetSetup();app.ExitForTest();app.ExitForTest();};
 app.StartupForTest();Check(reentered && app.Shutdowns==1 && MainWindow.Constructed==0 && VaultDatabase.Current.Creates==0,"reentrant_setup_initialization_has_one_owner");
}
{
 var app=Fresh();app.ContinueVaultResetSetup();app.ContinueVaultResetSetup();
 Check(MainWindow.Constructed==1 && VaultDatabase.Current.Creates==1 && app.Shutdowns==0,"successful_reset_continuation_is_not_repeated");
}
{
 var app=Fresh();var db=VaultDatabase.Current;bool deferred=false;
 VaultDatabase.OnCreate=()=>{app.ExitForTest();app.ExitForTest();deferred=app.Shutdowns==0 && db.InFlight;};
 app.StartupForTest();Check(deferred && app.Shutdowns==1 && MainWindow.Constructed==0 && db.Creates==1 && db.Committed.SequenceEqual(new byte[]{4,5,6}) && db.Resets==0 && db.Quarantines==0 && StartupService.Applies==0,"inflight_creation_finishes_preserves_commit_then_exits_without_continuation");
}
{
 var app=Fresh(VaultStorageState.PresentNeedsAuthentication);var db=VaultDatabase.Current;
 Window.OnDialog=w=>{if(w is UnlockWindow unlock)unlock.ResetRequested=true;app.ExitForTest();};
 app.StartupForTest();Check(db.Resets==0 && db.Creates==0 && MainWindow.Constructed==0,"startup_exit_does_not_consume_stale_reset_consent");
}
{
 var app=Fresh(VaultStorageState.IncompleteOrUnsupported);var db=VaultDatabase.Current;
 Window.OnDialog=w=>{if(w is VaultFreshStartWindow)app.ExitForTest();};
 app.StartupForTest();Check(db.Quarantines==0 && db.Creates==0 && MainWindow.Constructed==0 && app.Shutdowns==1,"fresh_recovery_confirmation_exit_does_not_quarantine");
}
{
 var app=Fresh(VaultStorageState.IncompleteOrUnsupported);var db=VaultDatabase.Current;
 Window.OnDialog=w=>{if(w is VaultRecoveryWindow recovery)recovery.Choice=VaultRecoveryWindow.RecoveryChoice.RestoreSqlite;};
 MessageBox.OnShow=()=>app.ExitForTest();app.StartupForTest();
 Check(db.Quarantines==0 && db.Creates==0 && MainWindow.Constructed==0,"sqlite_recovery_exit_does_not_restore_or_quarantine");
}
{
 var app=Fresh(VaultStorageState.LegacyUpgradeRequired);MessageBox.OnShow=()=>app.ExitForTest();app.StartupForTest();
 Check(VaultDatabase.Current.Creates==0 && MainWindow.Constructed==0,"legacy_upgrade_confirmation_exit_does_not_upgrade");
}
{
 var app=Fresh();VaultDatabase.OnInspect=()=>app.ExitForTest();app.StartupForTest();
 Check(VaultDatabase.Current.Creates==0 && MainWindow.Constructed==0 && app.Shutdowns==1,"inspection_cancellation_prevents_setup");
}
foreach(var state in new[]{VaultStorageState.Absent,VaultStorageState.PresentNeedsAuthentication}) {
 var app=Fresh(state);app.StartupForTest();Check(MainWindow.Constructed==1 && app.Shutdowns==0 && VaultDatabase.Current.IsUnlocked,$"healthy_{state}_still_opens_main");
}
{
 var app=Fresh(VaultStorageState.IncompleteOrUnsupported);var db=VaultDatabase.Current;int prompts=0,warnings=0;
 Window.OnDialog=w=>{if(w is VaultRecoveryWindow recovery)recovery.Choice=VaultRecoveryWindow.RecoveryChoice.Portable;if(w is SecretPromptWindow && ++prompts==3)app.ExitForTest();};
 MessageBox.OnShow=()=>warnings++;app.StartupForTest();
 Check(prompts==3 && warnings==0 && db.Quarantines==0 && db.Creates==0 && MainWindow.Constructed==0,"portable_confirmation_exit_does_not_open_another_modal");
}
{
 var app=Fresh();bool deferred=false;MainWindow.OnConstruct=()=>{app.ExitForTest();deferred=app.Shutdowns==0;};
 app.StartupForTest();var shell=app.WindowHistory.OfType<MainWindow>().Single();
 Check(deferred && app.Shutdowns==0 && shell.Closed && !shell.IsVisible && shell.Drains==1,"constructor_reentrancy_uses_unloaded_shell_cleanup_without_show");
}
{
 var app=Fresh();MainWindow.OnConstruct=()=>{app.Windows.OfType<MainWindow>().Single().CleanupSucceeds=false;app.ExitForTest();};
 app.StartupForTest();var shell=app.WindowHistory.OfType<MainWindow>().Single();
 Check(app.Shutdowns==0 && !shell.Closed && !shell.IsVisible && shell.Notifications==1,"constructor_cancel_cleanup_refusal_keeps_shell_alive");
 shell.CleanupSucceeds=true;app.ExitForTest();Check(shell.Closed && shell.Drains==2 && app.Shutdowns==0,"constructor_cancel_cleanup_refusal_remains_retryable");
}
foreach(var command in new[]{"EXIT_FOR_INSTALL","EXIT_FOR_BUILD","NOT_AN_EXIT"}) {
 var app=Fresh();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));var listener=app.ListenForTest(stop.Token);
 using(var client=new System.IO.Pipes.NamedPipeClientStream(".",App.PipeForTest,System.IO.Pipes.PipeDirection.Out)){
  await client.ConnectAsync(stop.Token);using var writer=new StreamWriter(client,System.Text.Encoding.UTF8,1024,leaveOpen:true){AutoFlush=true};await writer.WriteLineAsync(command);
 }
 var until=DateTime.UtcNow.AddSeconds(1);while(DateTime.UtcNow<until && app.Shutdowns==0)await Task.Delay(10);
 stop.Cancel();await listener;Check(app.Shutdowns==(command=="NOT_AN_EXIT"?0:1),$"actual_pipe_{command}_routing");
}
{
 var app=Fresh(VaultStorageState.LegacyUpgradeRequired);int messages=0;
 MessageBox.OnShow=()=>{if(++messages==2)app.ExitForTest();};app.StartupForTest();
 Check(messages==2 && VaultDatabase.Current.Creates==1 && ProfileVaultService.Unseals==0 && MainWindow.Constructed==0,"post_upgrade_confirmation_exit_prevents_profile_continuation");
}
{
 var app=Fresh(VaultStorageState.IncompleteOrUnsupported);int messages=0;
 Window.OnDialog=w=>{if(w is VaultRecoveryWindow recovery)recovery.Choice=VaultRecoveryWindow.RecoveryChoice.Portable;};
 VaultDatabase.OnCreate=()=>app.ExitForTest();MessageBox.OnShow=()=>messages++;app.StartupForTest();
 Check(messages==0 && VaultDatabase.Current.Creates==1 && VaultDatabase.Current.Committed.SequenceEqual(new byte[]{4,5,6}) && ProfileVaultService.Unseals==0 && MainWindow.Constructed==0,"inflight_portable_recovery_preserves_commit_without_late_modal_or_profiles");
}
foreach(var retained in new[]{true,false}) {
 var app=Fresh(VaultStorageState.IncompleteOrUnsupported);var db=VaultDatabase.Current;db.MissingMain=true;db.RetainedStage=retained;int messages=0;var before=db.Committed.ToArray();
 MessageBox.OnShow=()=>{messages++;app.ExitForTest();};app.StartupForTest();
 Check(messages==1 && db.Committed.SequenceEqual(before) && db.Creates==0 && db.Quarantines==0 && MainWindow.Constructed==0 && app.Shutdowns==1,$"retained_{(retained?"creation":"orphaned_sidecars")}_exit_preserves_without_late_modal");
}
{
 var app=Fresh();var shell=new MainWindow{IsLoaded=false,SaveSucceeds=false};shell.SetDirty(true);app.MainWindow=shell;var db=VaultDatabase.Current;bool deferred=false;
 VaultDatabase.OnCreate=()=>{app.ExitForTest();deferred=app.Shutdowns==0 && db.InFlight;};
 app.ContinueVaultResetSetup();
 Check(deferred && app.Shutdowns==0 && !shell.Closed && shell.Notifications==1 && db.Committed.SequenceEqual(new byte[]{4,5,6}),"reset_initialization_exit_retains_unloaded_shell_refusal_after_commit");
 shell.SaveSucceeds=true;app.ExitForTest();Check(shell.Closed && shell.Drains==1 && app.Shutdowns==0,"reset_initialization_exit_unloaded_shell_refusal_is_retryable");
}
foreach(var kind in new[]{"healthy","normal_cancel","installer_cancel"}) {
 var app=Fresh(VaultStorageState.PresentNeedsAuthentication);var old=new MainWindow();app.MainWindow=old;old.Show();bool entered=false,closedAtEntry=false;
 Window.OnDialog=w=>{
  entered=true;closedAtEntry=old.Closed && !app.Windows.Contains(old) && old.ExitingForTest && old.PermitForTest && app.ShutdownMode==ShutdownMode.OnExplicitShutdown;
  if(kind=="normal_cancel")w.Close();else if(kind=="installer_cancel")app.ExitForTest();
 };
 await old.ResetForTest();
 Check(entered && closedAtEntry && VaultDatabase.Current.Resets==1,$"actual_reset_{kind}_establishes_closed_exiting_owner_before_setup");
 Check(kind=="healthy"?app.Shutdowns==0 && MainWindow.Constructed==2 && app.Windows.OfType<MainWindow>().Single()!=old && VaultDatabase.Current.Creates==1:
  app.Shutdowns==1 && MainWindow.Constructed==1 && app.Windows.OfType<MainWindow>().Count()==0 && VaultDatabase.Current.Creates==0,$"actual_reset_{kind}_completes_or_exits_without_closed_owner_interception");
 if(kind!="healthy") {app.ExitForTest();app.ExitForTest();Check(app.Shutdowns==1 && MainWindow.Constructed==1 && old.Drains==0,$"actual_reset_{kind}_exit_retries_are_idempotent");}
}
foreach(var loaded in new[]{false,true}) {
 var app=Fresh(VaultStorageState.PresentNeedsAuthentication);var live=new MainWindow{IsLoaded=loaded,SaveSucceeds=false};live.SetDirty(true);app.MainWindow=live;VaultDatabase.Current.IsUnlocked=true;
 app.ExitForTest();Check(app.Shutdowns==0 && app.Windows.Contains(live) && !live.Closed && live.Notifications==1,$"live_{(loaded?"loaded":"constructed_unloaded")}_membership_retains_save_refusal");
 live.SaveSucceeds=true;app.ExitForTest();Check(live.Closed && !app.Windows.Contains(live) && live.ExitingForTest && live.PermitForTest && app.Shutdowns==0,$"live_{(loaded?"loaded":"constructed_unloaded")}_retry_completes_real_close_transition");
}
foreach(var answer in new[]{MessageBoxResult.Yes,MessageBoxResult.No}) {
 var app=Fresh(VaultStorageState.IncompleteOrUnsupported);int pickersConstructed=0,pickersShown=0,extraDialogs=0,extraMessages=0;bool cancelled=false;
 Microsoft.Win32.OpenFileDialog.OnConstruct=()=>{if(cancelled)pickersConstructed++;};Microsoft.Win32.OpenFileDialog.OnShow=()=>{if(cancelled)pickersShown++;};
 Window.OnDialog=w=>{if(cancelled)extraDialogs++;if(w is VaultRecoveryWindow recovery)recovery.Choice=VaultRecoveryWindow.RecoveryChoice.RestoreSqlite;};
 MessageBox.Result=answer;MessageBox.OnShow=()=>{if(cancelled)extraMessages++;app.ExitForTest();cancelled=true;};app.StartupForTest();
 Check(cancelled && app.Shutdowns==1 && pickersConstructed==0 && pickersShown==0 && extraDialogs==0 && extraMessages==0 && VaultDatabase.Current.Quarantines==0 && VaultDatabase.Current.Creates==0 && MainWindow.Constructed==0,$"sqlite_confirmation_{answer}_exit_constructs_and_shows_zero_new_modals");
 Microsoft.Win32.OpenFileDialog.OnConstruct=null;Microsoft.Win32.OpenFileDialog.OnShow=null;
}
return failures==0?0:1;
