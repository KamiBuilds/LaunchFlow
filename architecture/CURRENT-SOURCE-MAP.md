# Current source and ownership map — v0.7.74 incremental candidate

**Reviewed parent:** `897825ff5f10a9181c9979465c4a5920aec2ecce` (v0.7.73). The new commit's exact identity is recorded in GitHub.

## Current operation path and owners

- **UI admission:** `UI/Shell/MainWindow.Execution.cs` starts an operation through `UI/Controllers/RunUiOperationController.cs`. That controller owns run leases and cancellation, rejecting queued stale status.
- **Login:** `Services/Automation/AutomationEngine.*.cs` owns the actual Playwright page, selected input, frame, document, HTML `input.form`, protected credential fill and completion checks. `Services/Login/LoginFlowCoordinator.cs` only sequences an attempt and reports bounded failure codes.
- **Approval:** `MainWindow.ConfirmLoginOriginAsync` marshals to WPF and displays the existing modal. `Services/Persistence/PasswordOriginApprovalCoordinator.cs` rechecks run, editor, configuration, vault and policy ownership and uses `ConfigurationSaveTransaction` to durably save a detached candidate before publishing an exact HTTPS password destination. The WPF adapter does not own browser sessions or keys.
- **Final password dispatch:** `AutomationEngine.Login.cs` retains separate initial interactive consent, ordinary origin permission, and *synchronous* `RequireCurrentPasswordDestination` directly before `ElementHandle.FillAsync`. The final check must not open windows, save or remap.
- **Editor/save:** `WorkflowEditorSession` owns reference selection, selection epoch, revision and dirty state; `ConfigurationSaveTransaction` distinguishes durable-but-superseded from published success. `VaultDatabase.Current.SessionGeneration` is the ambient vault authorization authority.
- **Diagnostics:** `RunIssueNotice.cs` centralizes safe codes, bounded fallback and progress copy; `RunOutcomeSummary.cs` handles mixed outcomes. No raw driver exceptions, full URLs or credential data are exported.
- **Exit:** Browser, profile ownership, seal/cleanup and sticky errors remain in engine and existing exit-safety services.

## Regression evidence and limitations

Native approval adapter body: `tests/workspaces/extract-ui-methods.py` and `UiTests.csproj`; coordinator and save transaction are compiled from production files. Real protected Chromium fill: `tests/runtime/DocumentBoundCredentialTests.cs`. Prior AUTO-03, SQLite autosave/reopen, backup-integrity, installation and exit gates must all remain green on the same candidate commit. These automated tests cannot establish actual vendor sign-in or interactive WPF acceptance on a user's device.

---

## Historical maps retained below

# Current source and state ownership map

**Structural implementation branch:** `refactor/core-structure-20261106` from approved baseline `93b49482b6ea197c810e27def9fe6d9b080ec0aa` (LaunchFlow v0.7.46). This overlay describes the implemented source at `a1fe9967c9b9dd9163870b680180e72f6689ece8` before final verification. Subsequent documentation/verification commits must be treated as distinct source bytes. The earlier baseline map is preserved below for historical context.

## 2026-11-06 structural ownership overlay — candidate, not accepted

| Component | Owned state or responsibility | Production call path and boundaries |
|---|---|---|
| [RunUiOperationController](../LaunchFlow/UI/Controllers/RunUiOperationController.cs) | Per-UI-run lease, stop-token reference, task reference and status phase | MainWindow acquires lease **before modal prompts**, registers real engine task after approval, releases only matching lease in finally; AutomationEngine separately owns browser and session lifetime |
| [WorkflowEditorSession](../LaunchFlow/UI/Controllers/WorkflowEditorSession.cs) | Current workflow object reference, edit revision, dirty state and specific-revision acknowledgement | MainWindow website/navigation/save/autosave adapters; does **not** own live Configuration, vault generation, workspace revision or Windows session |
| [WebsiteEditMapper](../LaunchFlow/Domain/Models/WebsiteEditMapper.cs) | Shared normalized assignment of every editable website property | Navigation uses ordered ApplyLeading → ApplyBrowser → ApplyLogin → AutoLoginCheck event → ApplyDetails; durable WebsiteEditDraft.Apply uses the same groups on a detached candidate, after ID/index and save-transaction guards |
| [LoginFlowCoordinator](../LaunchFlow/Services/Login/LoginFlowCoordinator.cs) | Transient nonsecret per-attempt sequencing and submission markers | AutomationEngine.TryAutoLoginAsync delegates; engine retains credential acquisition, policy, browser, cancel and public-result authority |
| [LoginCompletionObserver](../LaunchFlow/Services/Login/LoginCompletionObserver.cs) | Bounded verification observations | Actual LoginHealth predicates and cancellation remain authoritative; submission is not automatically verified completion |
| [AutomationEngine.LoginStages](../LaunchFlow/Services/Automation/AutomationEngine.LoginStages.cs) | Named username, combined/resumed password and owned final-submit stages | Protected FillCredentialAsync afterFill callback keeps document/form/frame binding alive through continuation; no separate context/session owner |

**Before/after physical source sizes** (GitHub recursive-tree blob byte sizes; not an assertion of fewer LOC): 18 MainWindow C# partials, **166,577 → 167,807 bytes**; new Controllers, **0 → 4,343 bytes**; three extracted login collaboration files, **0 → 23,353 bytes**; tracked files **261 → 269**. Responsibility extraction, not a universal line budget, is the measure; MainWindow remains the WPF dialog, rendering, dispatcher and platform lifecycle adapter.

**Reentrant save hardening at `95c0b866`:** `WorkflowEditorSession.CaptureOwnership` records the workflow reference, edit revision and a separate selection epoch. The epoch changes when selecting another object, so an A → B → same A transition without intervening edits cannot reuse an earlier durable-save owner. `SaveAll` tests the entire token when committing and before marking that revision saved. The run lease and editor selection epoch remain separate from vault/workspace generation.

**Former mutable responsibilities removed from MainWindow:** run admission (`_runInProgress`), status phase (`_runStatusActive`), run cancellation (`_runCancellation`) and engine task (`_activeRunTask`) are accessed via RunUiOperationController. Selected workflow (`_selected`), edit generation (`_editGeneration`) and dirty acknowledgement (`_hasUnsavedChanges`) are accessed via WorkflowEditorSession. Readers/writers: RunEnabledAsync admits/registers/completes a lease, Stop/lock/exit cancel through it, queued workflow status checks its captured lease; website selection selects the editor object, edit events mark a revision, SaveAll acknowledges only its captured revision. Existing config/workspace/vault/session authorities stay separate.

### Editable website field-to-consumer contract

Both consumers use the same typed mapper, with deliberately different evaluation timing: N = navigation `MainWindow.Persistence.ApplyCurrentEditor`; D = detached durable `WebsiteEditDraft.Apply` invoked from `CaptureConfigurationDraft` / `ConfigurationSaveTransaction`. Every entry below is exercised by `tests/workspaces/WebsiteEditMapperTests.cs` (W1 full sentinel + W2 browser partial + W3 monitor + W4 normalization); event-order/native WPF tests remain separate.

| WPF source input or decision | Shared rule / stage | Model target | Production consumers | Test |
|---|---|---|---|---|
| EnabledCheck | ApplyLeading: Boolean | Enabled | N,D | W1 |
| NameBox | NormalizeName: trim, blank → Unnamed website | Name | N,D | W1,W4 |
| StartUrlBox | NormalizeText: trim | StartUrl | N,D | W1 |
| DestinationUrlBox | NormalizeText: trim | DestinationUrl | N,D | W1 |
| BrowserCombo | BrowserSelection, ApplyBrowser: reject missing | Browser | N,D | W1,W2 |
| MonitorCombo + _monitorSelectionChanged | ReplaceMonitor only on explicit choice/allowed missing identity | Replacement decision | N,D | W3 |
| MonitorCombo.Index | ApplyBrowser: retain saved identity when not replaced | MonitorIndex | N,D | W1,W3 |
| MonitorCombo.DeviceName | ApplyBrowser: retain disconnected saved name when not replaced | MonitorDeviceName | N,D | W1,W3 |
| LoginModeCombo | SelectedLoginMode, ApplyLogin | LoginMode | N,D | W1 |
| LoginModeCombo (derived) | WebsiteLoginMode.UsesSavedPassword; UI AutoLoginCheck fires in N after ApplyLogin | AutoLogin | N,D | W1, UI autosave |
| UsernameContinueBox | NormalizeText, ApplyLogin | UsernameContinueSelector | N,D | W1 |
| PasswordOriginBox | NormalizeText, ApplyLogin | PasswordCredentialOrigin | N,D | W1 |
| KeepassEntryBox | NormalizeText, ApplyDetails | KeepassEntryPath | N,D | W1 |
| UsernameSelectorBox | NormalizeText, ApplyDetails | UsernameSelector | N,D | W1 |
| PasswordSelectorBox | NormalizeText, ApplyDetails | PasswordSelector | N,D | W1 |
| SubmitSelectorBox | NormalizeText, ApplyDetails | SubmitSelector | N,D | W1 |
| CredentialOriginBox | NormalizeText, ApplyDetails | CredentialOrigin | N,D | W1 |
| LoginSuccessSelectorBox | NormalizeText, ApplyDetails | LoginSuccessSelector | N,D | W1 |
| LoginSuccessUrlBox | NormalizeText, ApplyDetails | LoginSuccessUrlContains | N,D | W1 |
| LoginWaitBox | NormalizeWait: culture-aware int.TryParse; invalid 60; clamp 5–180 | LoginWaitSeconds | N,D | W1,W4 |

WPF credential/password inputs, workflow ID/index ownership, steps, unknown configuration extensions and global settings are intentionally outside this mapping. Invalid global settings still block durable Save capture but not live navigation. The navigation path preserves its original leading-field partial update if browser validation fails.

**Verification:** New tests `tests/workspaces/OperationOwnershipTests.cs` and `WebsiteEditMapperTests.cs` plus current UI/autosave/runtime extraction compile the collaborators. First branch CI at `40b22f98` failed on CS0165 in a regression test; corrected by `a1fe9967c9b9dd9163870b680180e72f6689ece8`. Hosted rerun, native installer/build, independent full-source review, owner interactive Windows acceptance and the final pinned artifact must each be recorded separately. Do not call an unexecuted gate passed.

---

## Historical baseline map retained for comparison

**Reviewed source baseline:** `52f0978b0954937a61674105c01b10e57d80f67c` (LaunchFlow 0.7.46). **Maintenance branch:** `refactor/architecture-readability-20261106`. This map describes code **at that baseline**; the delivery receipt supplies the final documentation-only commit SHA. Links target repository paths at the baseline. Partial classes share one object and all private fields; file separation is navigation, **not** service isolation.

## Start here: composition and dependency ownership

- [App.xaml.cs](../LaunchFlow/App.xaml.cs), `InitializeVault`: inspect absent/legacy/v3/incomplete storage; require setup/unlock or separately consented legacy upgrade; allow profile restoration only after authorization. An incomplete vault must remain intact.
- [MainWindow.xaml.cs](../LaunchFlow/UI/Shell/MainWindow.xaml.cs), constructor: obtain configuration from `ConfigStore`, initialize `CredentialVault`, construct `AutomationEngine`, and wire the injected delegates of `WorkflowConfigurationService`, `ConfigurationSaveTransaction` and `StartupRegistrationService`. These are useful *local* seams, not a general DI graph.
- Ambient authorities remain: `VaultDatabase.Current` for master key, authenticated session generation, configuration and credential persistence; `EnterprisePolicy.Current` for live policy; `ProfileVaultService` for profile sealing/unsealing; OS startup/session facilities for Windows events. `CredentialVault` is an adapter for locally stored or explicitly configured KeePassXC credentials, **not** a second encrypted database owner.
- [VaultRecordCodec.cs](../LaunchFlow/Services/Vault/VaultRecordCodec.cs), [VaultDatabase.Strict.cs](../LaunchFlow/Services/Vault/VaultDatabase.Strict.cs), [VaultDatabase.Upgrade.cs](../LaunchFlow/Services/Vault/VaultDatabase.Upgrade.cs): v3 purpose/version/expected workflow and vault-ID AAD; strict-authoritative tables; consented legacy migration plus verified recovery copy. No documentation slice changes codec or storage format.

## Window entrypoint and call map

| User or event | Entry and ownership path | Important boundary |
|---|---|---|
| Edit website | [MainWindow.Websites.cs](../LaunchFlow/UI/Shell/MainWindow.Websites.cs) `WorkflowList_SelectionChanged`, [MainWindow.WebsiteEditor.cs](../LaunchFlow/UI/Shell/MainWindow.WebsiteEditor.cs), [MainWindow.Persistence.cs](../LaunchFlow/UI/Shell/MainWindow.Persistence.cs) | `ApplyCurrentEditor` maps controls to a **live** selected workflow for navigation. Not an encrypted credential save |
| Navigate tabs/settings | [MainWindow.Navigation.cs](../LaunchFlow/UI/Shell/MainWindow.Navigation.cs) `MainTabs_SelectionChanged` → `ApplyCurrentEditor` | Save pending editor values to current in-memory model. Do not substitute a durable `SaveAll` transaction |
| Manual Save | [MainWindow.Credentials.cs](../LaunchFlow/UI/Shell/MainWindow.Credentials.cs) `Save_Click` → `SaveAll` in [Persistence](../LaunchFlow/UI/Shell/MainWindow.Persistence.cs) | Detached draft + durable configuration commit before in-place publication |
| Autosave | [MainWindow.AutoSave.cs](../LaunchFlow/UI/Shell/MainWindow.AutoSave.cs) `AutoSaveTimer_Tick` → `SaveAll` | Debounce and authorization gate; unsuccessful attempt remains unsaved |
| Password save/remove | [MainWindow.Credentials.cs](../LaunchFlow/UI/Shell/MainWindow.Credentials.cs) `SaveCredentials_Click` / `ClearCredential_Click` → vault credential transaction | Explicit per-workflow authorization/selection, modal revalidation; never inferred from normal website-field mapping |
| Run | [MainWindow.Execution.cs](../LaunchFlow/UI/Shell/MainWindow.Execution.cs) `Run_Click`, `RunSelected_Click` → `RunEnabledAsync` → [AutomationEngine.cs](../LaunchFlow/Services/Automation/AutomationEngine.cs) `RunAsync` | Window admits UI operation; engine independently admits browser lifetime. Revalidate configuration/workspace/selection after saves and awaits |
| Login | [AutomationEngine.Login.cs](../LaunchFlow/Services/Automation/AutomationEngine.Login.cs) `RunOneAsync` → `TryAutoLoginAsync`, `VerifyBrowserLedSsoAsync`, `LoginHealth`, then steps and destination | Browser/page/frame/form and policy ownership must remain valid at credential read/fill/submit; no global locator or stale approval |
| Teach / Repair | [MainWindow.Execution.cs](../LaunchFlow/UI/Shell/MainWindow.Execution.cs) `RecordSteps_Click`, `RepairLogin_Click` / `RepairLoginCoreAsync`, `ShowRecorderAsync` → [TeachRecorder.cs](../LaunchFlow/Services/Teach/TeachRecorder.cs) | Record against captured workflow/config/workspace/vault generation; publish only if still owned. Recorder and Teach task must drain before profile sealing |
| Import/export | [MainWindow.Transfer.cs](../LaunchFlow/UI/Shell/MainWindow.Transfer.cs) `ImportLaunchFlowFile`, `Export_Click` → [SecureTransferService.cs](../LaunchFlow/Services/Transfer/SecureTransferService.cs), `VaultDatabase.ReplaceSnapshot` / `CaptureExportSnapshot` | Revalidate after dialogs and password prompts; imported workflows disabled until owner review; ciphertext rebound to destination |
| Vault lock | [MainWindow.VaultLocking.cs](../LaunchFlow/UI/Shell/MainWindow.VaultLocking.cs) `SecureVaultLockAsync` → `SecureVaultLockCoreAsync` → [AutomationEngine.cs](../LaunchFlow/Services/Automation/AutomationEngine.cs) `CloseSessionsAsync` | Revoke key first; cancel run; stop recorder; close contexts; await run/Teach; seal profiles **only when cleanup safe** |
| Windows lock/unlock | [MainWindow.WindowsSession.cs](../LaunchFlow/UI/Shell/MainWindow.WindowsSession.cs) `HandleSessionSwitch`, `LockAfterThresholdAsync`, `OnWindowsUnlockedAsync` | Duplicate events ignored; grace period keeps authorization only within configured interval; delayed unlock run is cancellable |
| Exit/tray/installer | [MainWindow.xaml.cs](../LaunchFlow/UI/Shell/MainWindow.xaml.cs) `ExitFromTray`, `OnClosing`, `RequestShutdownForInstallAsync` → [MainWindow.Lifecycle.cs](../LaunchFlow/UI/Shell/MainWindow.Lifecycle.cs) `CompleteOwnedExitAsync` → [ExitCompletionGate.cs](../LaunchFlow/Services/Workspaces/ExitCompletionGate.cs) | Reject pending edits, dialogs or incomplete cleanup. Permit close only after owned lock, drain, seal and runner disposal |

## Two different editing contracts — do not conflate them

**Durable Save** (see [MainWindow.Persistence.cs](../LaunchFlow/UI/Shell/MainWindow.Persistence.cs), [ConfigurationEditDraft.cs](../LaunchFlow/Domain/Models/ConfigurationEditDraft.cs), [ConfigurationSaveTransaction.cs](../LaunchFlow/Services/Persistence/ConfigurationSaveTransaction.cs)):

`Controls → CaptureConfigurationDraft (detached immutable values) → clone live Configuration → draft.Apply(candidate) → validate → authorization/edit-generation recheck → ConfigStore.SaveDurable(candidate, vault SessionGeneration) → ownership-checked draft.Apply(live) → MarkSaved → OS startup registration + UI refresh`.

Late ownership changes are distinct outcomes: `SupersededBeforeWrite`, `CommittedButSuperseded`, and `CommittedButPublicationFailed` must **not** be presented as equivalent to storage failure. OS startup registration/presentation may fail after durable commit; do not claim rollback. Unknown configuration/workflow extensions survive the clone/commit boundary.

**Navigation editing** (see [MainWindow.Navigation.cs](../LaunchFlow/UI/Shell/MainWindow.Navigation.cs) and [MainWindow.Websites.cs](../LaunchFlow/UI/Shell/MainWindow.Websites.cs)):

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
| `_teachUi` (`TeachUiOperationController`), `_activeRecorder` | Teach/Repair lease + tracked task; execution partial begins/completes lease, lock core drains `ActiveTask` | Dispatcher ownership, cancellation/close barriers; stale workflow/revision/session discards Teach publication |
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
