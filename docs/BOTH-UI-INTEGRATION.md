# Selective UI integration for `both`

## Source and boundaries

- Foundation: `c3451c26208f75f3fb57924e3dfe1db8cfc8985e`.
- Compared source: `origin/smartest-app-developer` resolved to
  `cecb1776b2825d5b5ffe991b9fc631c3d34f8d8c` (not a blind MainWindow replacement).
- The actual config model is `LaunchFlow/Models/Configuration.cs`, not AppConfig.cs.
  Monitor files are `Models/MonitorChoice.cs` and `Services/MonitorService.cs`.
- Production engine, recorder services/window, vault/encryption implementation,
  profile wrapping, packaging, application version, installer identity and providers
  are unchanged by this slice. No real vaults or credentials are used in tests.

## Included behavior

- Named workspaces store their own stable ID, display name and existing workflow
  IDs only. Saving captures enabled workflows; updating a case-insensitive name
  retains workspace identity and requires UI confirmation. Failed persistence rolls
  the capture back. Running preserves saved order, ignores deleted/disabled IDs and
  duplicates, and never enables a disabled workflow implicitly.
- Config adds `Workspaces` (empty for old configs) and `UiLanguage` (`auto` by
  default). Config, workflow and workspace JSON retain unknown extension fields.
  Existing settings, workflow IDs, approved login origins and steps round-trip.
  Optional null workspace/ID collections deserialize as empty collections.
- Monitor choices include Windows `Screen.DeviceName` and primary status.
  Resolution prefers the saved device name case-insensitively, supports legacy
  indices, and uses primary/first/synthetic bounds when no saved display exists.
  Fallback does not erase an unplugged device identity on unrelated editor saves.
  Explicit monitor edits persist the new choice; duplication retains its identity.
- Navigation/workspace/run controls have offline English/Dutch text, corresponding
  UI Automation names, tooltips, WPF language metadata and a polite status region.
  Settings chooses automatic/English/Dutch. This is deliberately **partial**
  localization: other help, forms and dialogs remain English, as stated in the UI.
- UI-level run ownership is established before modal prompts. Run/Test/workspace/
  Teach/Repair buttons cannot start competing work; Stop and cancellation cleanup
  restore controls in `finally`. Test with no selection no longer runs unrelated
  workflows. A nonzero existing engine `FailedCount` is not announced as success.
- Vault locking revokes the key and clears sensitive editor authorization before
  awaiting slow operations. Concurrent lock requests share one task; cancelled
  engine/Teach-open operations finish before session teardown. Browser cleanup is
  awaited without blocking the dispatcher, and profile sealing runs off-dispatcher.
  Reauthorization is blocked during cleanup. Existing profile-unseal failure
  warnings and retained-file safety are preserved.
- Modal unlock and KeePass run prompts recheck authorization after returning.
  Pending Teach saves are discarded after authorization loss. The hermes modeless
  Teach binding still saves to the original workflow ID after selection changes;
  a deleted original is still rejected and persistence failures still roll back.

## Engine/integration contracts for the parent

This slice builds against the foundation engine; it does **not** require or invent
EnterprisePolicy, workflow-status events, cancellation-enabled recorder APIs, or a
new VisualRecorderWindow constructor.

1. MainWindow calls existing `RunAsync(..., includeDisabled, configuration,
   keepassMasterPassword)`, reads existing `RunResult.FailedCount` and
   `LoginNeedsAttention`, and awaits existing `CloseSessionsAsync()`.
2. MainWindow refreshes runtime `MonitorIndex` with
   `MonitorService.Resolve(workflow, MonitorService.GetMonitors()).Index` immediately
   before a run. For topology changes **during** a run, the engine should resolve
   again at placement time: pass `WebsiteWorkflow` into its placement helper and
   select `MonitorService.Resolve(workflow, liveMonitors).Bounds`. Do not rely on the
   pre-run index alone. `DeviceName` is a Windows logical display identity, not an
   EDID-based guarantee across display-driver replacement/reinstallation.
3. Teach still calls `new VisualRecorderWindow(page)` because that is the foundation
   API. If the parent adds `(page, workflow)`, change this single construction while
   retaining `workflow.Id` lookup after the modeless await and authorization check.
4. UI status is run-level only. If the parent supplies a per-workflow event, marshal
   it through the dispatcher and localize its states; no such event is assumed here.
   Explicit SSO-mode controls were not ported ahead of compatible engine behavior.
5. Cancellation revokes permission for new UI work and clears vault authorization;
   it does not retract secret copies or browser calls the engine already dispatched.
   Slow/noncancellable engine operations may delay browser closure/profile sealing,
   but do not keep the vault authorized or synchronously block the UI. Parent engine
   document-binding/cancellation tests remain a separate gate.
6. Imported workspace/language/extension fields are copied into the live UI config.
   Existing transaction-based replacement and no automatic imported hotkey/startup
   registration remain. This is not a replacement for the security worker's import
   validation and policy enforcement.

## Executed verification

Evidence is retained outside the repository at
`/root/projects/launchflow-evidence/both/ui/`.

```bash
DOTNET=/root/.cache/launchflow/dotnet/dotnet bash tests/workspaces/run.sh
# 8/8 linked-production config/workspace/monitor/localization tests passed.
# 10/10 extracted-production MainWindow behavioral seam tests passed.

/root/.cache/launchflow/dotnet/dotnet build LaunchFlow/LaunchFlow.csproj \
  --configuration Release -p:EnableWindowsTargeting=true -p:TreatWarningsAsErrors=true
# Build succeeded: 0 warnings, 0 errors.

/root/.cache/launchflow/dotnet/dotnet run \
  --project tests/profiles/ProfileTests.csproj --configuration Release
# Profile tests passed (simulated DPAPI contract).
```

Numbered RED/GREEN logs retain the actual missing-feature/regression failures and
passing reruns. `24-final-workspaces-ui.log`, `25-final-cross-build.log` and
`26-profile-regression.log` record the final verification runs. The UI harness
extracts unchanged current method bodies; vault/engine/controls/recorder dialogs
and monitor enumeration are explicit simulated seams, not native assurance.

## Remaining native Windows gates

- Open existing encrypted configs and fresh configs on Windows; save/restart and
  export/import workspace selections with synthetic logins; confirm IDs, settings
  and credentials are retained and imported workflows stay disabled until enabled.
- Validate workspace combo editing, overwrite confirmation and failed-save recovery;
  selected disabled workflow Test versus enabled-only workspace behavior.
- Dock/undock/reorder monitors before and during runs; verify actual browser window
  placement and retained unplugged preference, including non-first primary monitor.
- Check English/Dutch text layout at minimum window size and high DPI, keyboard
  navigation, screen-reader names/language and actual live-region announcements.
- Exercise delayed/stalled browser close, repeated/manual/Windows lock/unlock,
  sleep/resume and exit during run/Teach/cleanup on the actual dispatcher. Verify
  native DPAPI profile sealing/restoration and retained source on failures.
- Complete modeless Teach while selecting/deleting another workflow, cancelling,
  locking the vault, closing the owner and repairing sign-in; parent must re-run
  real browser/recorder and document-bound credential tests on integrated bytes.
- Native packaged installation/update and startup/hotkey/session-switch behavior
  remain separate from Linux cross-compilation. No release is certified here.
