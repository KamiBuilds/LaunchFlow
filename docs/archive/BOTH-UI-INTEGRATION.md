# Combined UI integration for `both`

## Scope and preserved foundation

This final UI slice targets combined parent `4d8faf1b183e7bfee35989ef930bcb62ba16e104`,
which already contains original repairs, document-bound credentials, workspace/UI,
enterprise policy and the engine close barrier. It is not a whole-window replacement.
Only MainWindow.xaml/.cs, App.xaml.cs crash recovery, tests/workspaces and this document
change. Engine, models, policy services, recorder, crypto formats/data locations,
workflow/workspace IDs, product/version/installer identity and providers are unchanged.

The inherited workspace, unknown JSON fields, localization selection and monitor
identity contracts remain covered by eight linked-production tests. Workspaces still
resolve enabled existing IDs in saved order without duplicates. Monitor fallback does
not erase disconnected display preference on unrelated saves. English/Dutch support
is **partial**: the existing Settings notice accurately says navigation/workspace
controls are translated and other text remains English. New detailed repair/security
messages remain English; this is not a full Dutch localization claim.

## Critical UI wiring

- Credential status gates policy-denied access and a locked vault before Has. A policy
  race reports unavailable rather than claiming that a credential is missing.
  Credential editing never reloads saved secrets into controls. Disabled read/fill
  blocks replacement-merge saves rather than interpreting an unreadable old value as
  empty/new. Actual production CredentialVault and EnterprisePolicy are linked in tests.
- KeePass decisions skip None and SSO, and never treat denied local access as a vault
  miss. Denial clears cached KeePass secrets. Run prompting, asynchronous entry listing
  and the entry-picker modal recheck fresh authorization/policy before retaining a
  password or changing persisted selection. Existing LaunchFlow credentials retain
  priority; no fallback bypass is introduced.
- Run, Teach and Repair entry points check fresh automation/Teach policy. Run repeats
  checks after unlock/KeePass modal boundaries; Teach checks after browser acquisition
  and the modeless Save await. Captured clicks still save to the original workflow ID,
  never the current selection; deleted originals and failed-save rollback remain intact.
- SSO origin consent captures the owning cancellation source, validates authorization,
  allowed URL/HTTPS policy and original workflow membership both before and after the
  modal. Rejected/stale consent neither adds ApprovedLoginOrigins nor persists.
  Failed persistence rolls back a newly added approval. Approval is not a rerun.
- Sign-in choices round-trip exact persisted `Auto`, `None`, `SSO` values rather than
  persisting a display label such as `Automatic`. Explicit SSO means browser sign-in
  without credential fill; normal MFA/SSO completion stays in the browser.
- OnWorkflowStatus uses BeginInvoke, not a synchronous dispatcher wait. Names are only
  display text, never repair identities. Cancellation-source ownership and an active
  status phase reject stale callbacks after final results or a later run.
- Final UI/tray summaries show failed workflow, login-attention and StepAttention
  counts together. Login attention cannot hide failures. Both repair paths locate
  workflows by WorkflowId, including duplicate names; positive login repair passes
  the captured workflow through its modal. Step repair selects the failed workflow
  for review. Nothing automatically reruns an action or workflow.

## Lock and crash cleanup contract

1. Publish a coalesced lock task before invoking synchronous cancellation callbacks.
2. Revoke the vault key first; clear pending secrets/editor authorization and keep
   new admissions blocked. Even cancellation callback reentrancy shares that task.
3. Await `CloseSessionsAsync` **before** observing pending UI run/Teach-acquisition
   tasks. The combined engine must cancel/close outstanding dispatched actions and
   provide a successful close barrier; waiting for a stalled fill before closing its
   context is the old deadlock-prone contract.
4. Only successful close allows profile sealing off-dispatcher. Failed close never
   calls SealAll: retain sources, leave the key revoked and admissions guarded, show
   a fixed status/tray warning and expose a title-bar Retry Lock vault button outside
   the disabled editor. A repeated lock retries cleanup. Pending task failures are
   observed separately after the barrier, so one fault does not skip the other task.
5. App fatal callbacks never zip/delete profiles. They perform best-effort key
   revocation and fixed user-facing diagnostics. A recoverable dispatcher exception
   requests the normal coordinated lock path; a terminating thread cannot safely
   await that path and leaves profile sources untouched. Fatal/native cleanup is
   explicitly **best effort**, not a guarantee that Chromium exited or data was sealed.

The historical UI test asserting run-before-close was intentionally replaced with
close-before-observe assertions, not deleted without evidence. Its inherited passing
output and the new real ordering/no-seal RED failures remain in the evidence directory.

## Exact parent requirements

- Retain EnterprisePolicy.Current snapshots, the DisableAutomation/Fill/Storage/Teach
  properties, RequireHttpsForCredentials and IsUrlAllowed. The actual policy service,
  not a UI reimplementation, owns parsing and fail-closed behavior. Registry edits
  while synchronous code executes are not an atomic UI transaction; the production
  vault/engine must continue enforcing policy at secret access and dispatch.
- Retain `OnWorkflowStatus: Action<string, WorkflowRunState>` with Running,
  WaitingForSignIn, Completed, Failed, Cancelled. Its string is a name, not a GUID.
  Keep RunResult.FailedCount, LoginNeedsAttention and StepsNeedAttention; attention
  entries must preserve WorkflowId/WorkflowName, and StepAttention.StepType must be
  a safe public action label. Do not replace ID-bound UI repair with name lookup.
- Keep existing RunAsync/GetPageForRecorderAsync and the modeless
  `VisualRecorderWindow(page)` API. CloseSessionsAsync must be an idempotent,
  cancellation/dispatch-aware barrier that completes only after owned contexts and
  dispatched operations are closed, rejects new admissions while closing/failed,
  retains ownership after failure and permits explicit cleanup retry. The UI seam
  simulates this contract; it does not prove the browser implementation.
- Parent independently reviews this commit, cherry-picks only after writers freeze,
  then reruns combined policy/runtime/profile/recorder tests and the actual app build.
  Preserve engine document/frame binding and key-revocation-first crypto behavior.
- Runtime monitor placement must resolve saved DeviceName against live monitors at
  placement time, not merely trust the UI's pre-run MonitorIndex. DeviceName is not
  an EDID guarantee across driver reinstall. This slice does not edit placement.

## Executed verification and evidence

Evidence: `/root/projects/launchflow-evidence/both/final-ui/` (earlier
`/root/projects/launchflow-evidence/both/ui/` logs remain historical and untouched).

```bash
DOTNET=/root/.cache/launchflow/dotnet/dotnet bash tests/workspaces/run.sh
# 8/8 linked-production workspace/monitor/localization tests.
# 28/28 extracted-production UI orchestration tests; real policy/credential services.

/root/.cache/launchflow/dotnet/dotnet build LaunchFlow/LaunchFlow.csproj \
  --configuration Release -p:EnableWindowsTargeting=true -p:TreatWarningsAsErrors=true
# Actual Windows application cross-build: 0 warnings, 0 errors.

git diff --check
```

Numbered evidence retains inherited ordering, actual RED and intermediate harness
failures separately. `02` captures no-seal/order RED, `05` policy/SSO/Teach RED,
`07` mixed-status/mode-mapping RED, `10` crash RED, `13` reachable-retry RED,
`14` asynchronous KeePass RED, `15` repair-identity RED, `17` reentrant key-revocation
and visible-warning RED, and `19` cached/post-modal KeePass RED. `03` exposed shared
project artifacts executing a stale sibling harness; Directory.Build.props and explicit
compile items fix that test infrastructure defect. Subsequent output proves both
executables run. Generated bodies are unchanged production bytes with explicit
native seams, not whole-WPF or real-browser tests. Final logs are `20-final-tests.log`
and `21-final-crossbuild.log`.

## Native Windows gates still required

- Real HKLM policy changes around modal unlock, origin consent, KeePass entry listing,
  modeless Teach Save and dispatched browser actions; no secret in disallowed frames,
  origins or documents. Run the real combined browser regression suite separately.
- Delayed/stalled browser close, repeated lock, failure/retry, Stop, Windows lock/unlock,
  sleep/resume and owner/process exit during run/Teach/cleanup on the actual dispatcher.
  Validate native DPAPI wrapping/restoration and retained profile sources on failures.
  Fatal callbacks cannot guarantee cleanup; test recovery with synthetic profiles.
- Native modeless recorder cancellation/navigation/closure and original workflow
  identity across selection/deletion. A recorder window awaiting user completion is
  not forcibly closed by this UI slice; authorization is rechecked before saving.
- Save/restart/import/export old encrypted data and workspace selections using
  synthetic credentials; verify IDs/settings/unknown fields and disabled imports.
- Dock/undock/reorder displays before/during runs, retained unplugged preference,
  minimum-width/high-DPI layout (including Retry Lock vault and SSO choices), keyboard
  access, screen-reader language and queued live-region announcements.
- Self-contained packaged installation/update, single-instance activation, hotkeys,
  startup and session-switch behavior remain native gates. No stable release, push,
  provider change or release certification is authorized by this work.
