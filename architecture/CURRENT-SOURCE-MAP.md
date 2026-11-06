# Current source and state ownership map

**Reviewed source baseline:** `52f0978b0954937a61674105c01b10e57d80f67c` (LaunchFlow 0.7.46). **Maintenance branch:** `refactor/architecture-readability-20261106`. This map describes code **at that baseline**; the delivery receipt supplies the final documentation-only commit SHA. Links target repository paths at the baseline. Partial classes share one object and all private fields; file separation is navigation, **not** service isolation.

## Start here: composition and dependency ownership

- [App.xaml.cs](../LaunchFlow/App.xaml.cs), `InitializeVault`: inspect absent/legacy/v3/incomplete storage; require setup/unlock or separately consented legacy upgrade; allow profile restoration only after authorization. An incomplete vault must remain intact.
- [MainWindow.xaml.cs](../LaunchFlow/MainWindow.xaml.cs), constructor: obtain configuration from `ConfigStore`, initialize `CredentialVault`, construct `AutomationEngine`, and wire the injected delegates of `WorkflowConfigurationService`, `ConfigurationSaveTransaction` and `StartupRegistrationService`. These are useful *local* seams, not a general DI graph.
- Ambient authorities remain: `VaultDatabase.Current` for master key, authenticated session generation, configuration and credential persistence; `EnterprisePolicy.Current` for live policy; `ProfileVaultService` for profile sealing/unsealing; OS startup/session facilities for Windows events. `CredentialVault` is an adapter for locally stored or explicitly configured KeePassXC credentials, **not** a second encrypted database owner.
- [VaultRecordCodec.cs](../LaunchFlow/Services/VaultRecordCodec.cs), [VaultDatabase.Strict.cs](../LaunchFlow/Services/VaultDatabase.Strict.cs), [VaultDatabase.Upgrade.cs](../LaunchFlow/Services/VaultDatabase.Upgrade.cs): v3 purpose/version/expected workflow and vault-ID AAD; strict-authoritative tables; consented legacy migration plus verified recovery copy. No documentation slice changes codec or storage format.

## Window entrypoint and call map

| User or event | Entry and ownership path | Important boundary |
|---|---|---|
| Edit website | [MainWindow.Websites.cs](../LaunchFlow/MainWindow.Websites.cs) `WorkflowList_SelectionChanged`, [MainWindow.WebsiteEditor.cs](../LaunchFlow/MainWindow.WebsiteEditor.cs), [MainWindow.Persistence.cs](../LaunchFlow/MainWindow.Persistence.cs) | `ApplyCurrentEditor` maps controls to a **live** selected workflow for navigation. Not an encrypted credential save |
| Navigate tabs/settings | [MainWindow.Navigation.cs](../LaunchFlow/MainWindow.Navigation.cs) `MainTabs_SelectionChanged` → `ApplyCurrentEditor` | Save pending editor values to current in-memory model. Do not substitute a durable `SaveAll` transaction |
| Manual Save | [MainWindow.Credentials.cs](../LaunchFlow/MainWindow.Credentials.cs) `Save_Click` → `SaveAll` in [Persistence](../LaunchFlow/MainWindow.Persistence.cs) | Detached draft + durable configuration commit before in-place publication |
| Autosave | [MainWindow.AutoSave.cs](../LaunchFlow/MainWindow.AutoSave.cs) `AutoSaveTimer_Tick` → `SaveAll` | Debounce and authorization gate; unsuccessful attempt remains unsaved |
| Password save/remove | [MainWindow.Credentials.cs](../LaunchFlow/MainWindow.Credentials.cs) `SaveCredentials_Click` / `ClearCredential_Click` → vault credential transaction | Explicit per-workflow authorization/selection, modal revalidation; never inferred from normal website-field mapping |
| Run | [MainWindow.Execution.cs](../LaunchFlow/MainWindow.Execution.cs) `Run_Click`, `RunSelected_Click` → `RunEnabledAsync` → [AutomationEngine.cs](../LaunchFlow/Services/AutomationEngine.cs) `RunAsync` | Window admits UI operation; engine independently admits browser lifetime. Revalidate configuration/workspace/selection after saves and awaits |
| Login | [AutomationEngine.Login.cs](../LaunchFlow/Services/AutomationEngine.Login.cs) `RunOneAsync` → `TryAutoLoginAsync`, `VerifyBrowserLedSsoAsync`, `LoginHealth`, then steps and destination | Browser/page/frame/form and policy ownership must remain valid at credential read/fill/submit; no global locator or stale approval |
| Teach / Repair | [MainWindow.Execution.cs](../LaunchFlow/MainWindow.Execution.cs) `RecordSteps_Click`, `RepairLogin_Click` / `RepairLoginCoreAsync`, `ShowRecorderAsync` → [TeachRecorder.cs](../LaunchFlow/Services/TeachRecorder.cs) | Record against captured workflow/config/workspace/vault generation; publish only if still owned. Recorder and Teach task must drain before profile sealing |
| Import/export | [MainWindow.Transfer.cs](../LaunchFlow/MainWindow.Transfer.cs) `ImportLaunchFlowFile`, `Export_Click` → [SecureTransferService.cs](../LaunchFlow/Services/SecureTransferService.cs), `VaultDatabase.ReplaceSnapshot` / `CaptureExportSnapshot` | Revalidate after dialogs and password prompts; imported workflows disabled until owner review; ciphertext rebound to destination |
| Vault lock | [MainWindow.VaultLocking.cs](../LaunchFlow/MainWindow.VaultLocking.cs) `SecureVaultLockAsync` → `SecureVaultLockCoreAsync` → [AutomationEngine.cs](../LaunchFlow/Services/AutomationEngine.cs) `CloseSessionsAsync` | Revoke key first; cancel run; stop recorder; close contexts; await run/Teach; seal profiles **only when cleanup safe** |
| Windows lock/unlock | [MainWindow.WindowsSession.cs](../LaunchFlow/MainWindow.WindowsSession.cs) `HandleSessionSwitch`, `LockAfterThresholdAsync`, `OnWindowsUnlockedAsync` | Duplicate events ignored; grace period keeps authorization only within configured interval; delayed unlock run is cancellable |
| Exit/tray/installer | [MainWindow.xaml.cs](../LaunchFlow/MainWindow.xaml.cs) `ExitFromTray`, `OnClosing`, `RequestShutdownForInstallAsync` → [MainWindow.Lifecycle.cs](../LaunchFlow/MainWindow.Lifecycle.cs) `CompleteOwnedExitAsync` → [ExitCompletionGate.cs](../LaunchFlow/Services/ExitCompletionGate.cs) | Reject pending edits, dialogs or incomplete cleanup. Permit close only after owned lock, drain, seal and runner disposal |

## Two different editing contracts — do not conflate them

**Durable Save** (see [MainWindow.Persistence.cs](../LaunchFlow/MainWindow.Persistence.cs), [ConfigurationEditDraft.cs](../LaunchFlow/Models/ConfigurationEditDraft.cs), [ConfigurationSaveTransaction.cs](../LaunchFlow/Services/ConfigurationSaveTransaction.cs)):

`Controls → CaptureConfigurationDraft (detached immutable values) → clone live Configuration → draft.Apply(candidate) → validate → authorization/edit-generation recheck → ConfigStore.SaveDurable(candidate, vault SessionGeneration) → ownership-checked draft.Apply(live) → MarkSaved → OS startup registration + UI refresh`.

Late ownership changes are distinct outcomes: `SupersededBeforeWrite`, `CommittedButSuperseded`, and `CommittedButPublicationFailed` must **not** be presented as equivalent to storage failure. OS startup registration/presentation may fail after durable commit; do not claim rollback. Unknown configuration/workflow extensions survive the clone/commit boundary.

**Navigation editing** (see [MainWindow.Navigation.cs](../LaunchFlow/MainWindow.Navigation.cs) and [MainWindow.Websites.cs](../LaunchFlow/MainWindow.Websites.cs)):

`Tab/website selection event → ApplyCurrentEditor → mutate fields of the previous selected live WebsiteWorkflow → select/render next editor`.

This is not the same timing or transaction contract as `SaveAll`; `ApplyCurrentEditor` performs no credential encryption/save. The password editor has explicit `SaveCredentials_Click`/clear controls. New fields must be reviewed in **both** mapping paths without merging their semantics.

## Run and browser ownership phases

`RunEnabledAsync` performs UI/modal admission (`_runInProgress`, Windows/vault/Teach/exit guards), attempts durable save, captures configuration/workspace revision and selected workflow, checks authorization again, then creates `_runCancellation` and calls engine `RunAsync`. The engine's `_running` is separately controlled under `_gate`; `_sessionGate` and tracked contexts/pages govern browser lifetime.

`AutomationEngine.RunOneAsync` phases: (1) validate start/destination URLs and live policy; (2) acquire/reuse and position page; (3) navigate or reuse same site; (4) manual confirmation/SSO or `TryAutoLoginAsync`; (5) inspect login health and optional success verification; (6) execute bound steps; (7) navigate destination, then report completion. `TryAutoLoginAsync` owns credential selection, staged username/password discovery, document/frame/form-bound field access, authorized submit, completion observation and manual-attention fallback. All awaits can invalidate ownership: do not treat a confirmed origin or loaded page as permanently trusted.

## Lock and exit order; sticky failure

`VaultDatabase.Lock → revoke UI run admission / cancel → stop recorder → AutomationEngine.CloseSessionsAsync → await run and Teach drain → ProfileVaultService.SealAll → release lock gate`. A failure to close or drain retains profiles and sets `_vaultCleanupFailed`; failure to seal sets `_profileSealFailed`. `_vaultLockInProgress` intentionally stays **true** when cleanup is incomplete. It is not a normal busy flag to reset in a `finally`. Retry via the existing Lock-vault recovery path after browser/profile conditions are safe; do not force deletion or run new automation. Ordinary/tray/installer exit uses `ExitCompletionGate` and refuses close if edits or cleanup are unsafe.

## Critical state owners and invalidation

| State | Meaning, writers and readers | Thread/lock, reset and failure |
|---|---|---|
| `_configuration` | Window's current live configuration; constructor loads, import replaces, Save publishes **in place**; UI/editor/run read | WPF dispatcher; Save checks original reference; import replacement invalidates captured owners |
| `_selected` | Current workflow instance, written on list/refresh/import selection; edit, credential, Teach and run read | Dispatcher; reference+membership rechecked after prompts; stale selection denies publication |
| `_workspaceRevision` | Workspace/selection shape revision, incremented by workspaces/import; run/import/Teach revalidate | Dispatcher; revision mismatch cancels stale action rather than using old targets |
| `_editGeneration` | Current editable revision; `MarkUnsavedChanges` increments; `SaveAll` captures and checks | Dispatcher; dirty edit after commit yields committed-but-superseded, not false saved state |
| `VaultDatabase.SessionGeneration` | Monotonic **vault** authorization identity, advanced on lock/unlock/rotation/upgrade; storage/transfer callers capture | `VaultDatabase._gate`; mismatch rejects stale write/credential use; not interchangeable with workspace/edit revision |
| `_runInProgress` | Window/modal admission for run, set/reset in `RunEnabledAsync`; button status/Teach/lock read | Dispatcher; reset after run `finally`; not equivalent to engine `_running` |
| `AutomationEngine._running` | Engine's independently synchronized active run; `RunAsync` owns set/reset; engine/browser admission reads | Engine `_gate` plus session teardown; failure/closing holds separate cleanup gates |
| `_runCancellation` | Token source for current UI-initiated engine run, created/cleared by `RunEnabledAsync` | Cancels on Stop, lock, Windows lock and exit; reference check prevents clearing a newer run |
| `_vaultLockInProgress` | Blocks run/UI during secure cleanup; `SecureVaultLockCoreAsync` writes, exit/run read | Set before cleanup; **remains true** if cleanup incomplete; retry Lock only after resolving blocker |
| `_vaultCleanupFailed` | Evidence cleanup failed and profiles must be retained; lock core sets, status/exit read | Sticky along with lock gate until a complete successful retry; never convert to success in finally |
| `_profileSealFailed` | Tracks failed encrypted profile sealing; lock core writes, installer/exit read | Failed seal blocks safe exit/resume; files retained for explicit recovery |
| `_windowsLocked` | Actual session lock admission, written only by session event handlers; run/Teach/save/exit read | Dispatcher; duplicate events ignored; timeout revokes vault at threshold and may prompt on unlock |
| `_exiting` | In-progress owned shutdown; `CompleteOwnedExitAsync` sets/resets on denial; other operations read | Dispatcher; reset only when exit permit revoked; failed cleanup prevents successful close |
| `_teachingInProgress`, `_activeTeachTask`, `_activeRecorder` | Teach/Repair task and recorder ownership; execution partial sets/clears, lock core drains | Dispatcher ownership, cancellation/close barriers; stale workflow/revision/session discards Teach publication |
| `_vaultLockTask`, `_exitDrain` | In-flight lock task and one-shot completion permit; vault locking/lifecycle writers, exit/UI readers | Shared async completion avoids duplicated close/lock; unsuccessful close revokes permit |

## Maintained test routes and limits

- [Architecture partial ownership](../tests/architecture/check_partials.py) — file/method budget and unique owners; not a complete dependency graph.
- [Service contracts](../tests/architecture/ServiceContracts.csproj) — linked model/save/startup/lock seams with injected dependencies; not native WPF.
- [Diagnostics](../tests/diagnostics/DiagnosticTests.csproj) — allowlisted export with a stub diagnostic writer; not actual CrashLog rotation.
- [UI + workspace](../tests/workspaces/UiTests.csproj), [UI verification](../tests/ui/verify_ui.py), [autosave](../tests/autosave/AutosaveIntegrationTests.csproj) — source-extracted/adapter fixtures, not interactive user acceptance.
- [Runtime browser](../tests/runtime/RuntimeTests.csproj), [recorder](../tests/recorder/RecorderTests.csproj) — isolated browser fixtures and platform simulations.
- [Storage/recovery](../tests/storage-integrity/StorageIntegrity.csproj), [S3 binding](../tests/s3-binding/S3Binding.csproj), [import](../tests/import-integrity/ImportIntegrity.csproj), [backup](../tests/backup-integrity/BackupIntegrity.csproj), [pinned real old writer/reader](../tests/policy/run-compatibility.sh) — synthetic encrypted SQLite and historical-format controls; not a real-vault migration.
- [Windows CI](../.github/workflows/windows.yml), [Windows UI CI](../.github/workflows/windows-ui-build.yml) — build/contract/native smoke when executed at the exact commit; **do not equate with owner-led interactive Windows acceptance**.

This documentation-only maintenance must not change executable tokens, XAML, resource data, credential format, lifecycle ordering or diagnostic schema.
