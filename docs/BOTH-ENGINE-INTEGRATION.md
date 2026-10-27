# Both-branch engine integration

This change combines selected behavior from `origin/smartest-app-developer` (`cecb177`) with the `hermes` engine at `4faa258`. It is not a wholesale engine replacement. It deliberately does **not** introduce an `EnterprisePolicy` or stable-monitor API dependency; those integrations belong to the parent change.

## Preserved safety boundaries

- Both username and password still use the unchanged `FillCredentialAsync` element/frame/document-bound approval and insertion helper from `ae1373b`.
- `tests/runtime/DocumentBoundCredentialTests.cs` is byte-identical to the starting worktree: SHA-256 `4369b66a25961ad954bbdbb0fa8af86bb2b4222081f70e2a6c61e758a49ef790`. Its delayed navigation observer remains 10000 ms; its 1200 ms navigation versus 800 ms continuation timing remains intact.
- A busy browser profile is rejected without deleting coordination locks, copying live profile databases or opening a replacement recovery profile.
- An unrestored sealed profile still blocks plaintext replacement-profile creation.
- There are no real account, credential-database or user-profile accesses in the runtime fixtures.

## Public UI contract

```csharp
public enum WorkflowRunState { Running, WaitingForSignIn, Completed, Failed, Cancelled }
public event Action<string, WorkflowRunState>? OnWorkflowStatus;
public sealed record StepAttention(Guid WorkflowId, string WorkflowName, string StepType);
// RunResult.StepsNeedAttention : List<StepAttention>
```

The enum and records are nested in `AutomationEngine`. The event's string is the **workflow name**, matching the newer branch's UI contract, not an ID. Marshal callbacks to the UI dispatcher without blocking them. Duplicate names are possible; use attention records' workflow IDs to select the correct workflow for repair. Keep event handlers nonthrowing and do not synchronously wait for the engine's close/run from a status callback.

For each started enabled workflow, events are `Running` then `Completed`, `WaitingForSignIn`, `Failed`, or `Cancelled`. Skipped disabled workflows have no events. Cancelling an active workflow cancels the returned run task; it does not return a successful partial `RunResult`. Workflows not started when cancellation arrives have no synthetic status event.

A failed recorded action increments `FailedCount`, adds `StepsNeedAttention`, emits `Failed`, and stops **that workflow** before any later action or destination navigation. Other workflows can still run. `StepType` is bounded to `Navigate`, `Click`, `Fill`, `Press`, `Wait`, or `Unsupported`; imported unknown step strings are never echoed. Unknown steps fail rather than being skipped. A generic non-step failure increments `FailedCount` without inventing a step repair record.

`LoginNeedsAttention` retains its existing shape. With `AutoLogin` enabled, `LoginMode = "SSO"` never calls the stored-credential path: it only inspects the current page for a manual sign-in/MFA challenge. Detected challenges pause later actions and destination navigation with `WaitingForSignIn`. No detected challenge permits continuation, but is **not authentication proof**. `LoginMode = "None"` or disabled `AutoLogin` bypasses automatic login inspection as before. Explicitly approved cross-origin form login in `Auto` mode remains supported and is distinct from the credential-free `SSO` mode.

Run-page reuse requires matching scheme and authority, not merely hostname. A scheme or port change navigates to the configured start URL. Same-origin open pages preserve existing sign-in progress. Teach continues to preserve an already open HTTP(S) page rather than navigating away from manual login progress.

Logs can contain the workflow's display label, but never raw Playwright exception messages, private action targets/values, query URLs, credential values or unsupported imported step text. Generic errors and step diagnostics use fixed public reasons/types. Consumers must not reintroduce raw exception/inner-exception rendering in their UI or diagnostics.

## Session ownership and locking contract

`CloseSessionsAsync` is an asynchronous **barrier**, not a permanent vault-locked mode:

1. Atomically exclude new run and Teach admissions; concurrent closes join the same task.
2. Cancel the linked active-run token. Cancellation callbacks are started asynchronously so a blocked run-unwinding subscriber cannot prevent context closure.
3. Acquire the single async lifecycle gate. Playwright initialization, session replacement, profile launch/claim, recorder acquisition/navigation, context cleanup and disposal use this gate.
4. A launch already in progress is awaited, not abandoned. After launch returns, recheck cancellation and closing **before inspecting pages**, and again before publishing the session. Close an unclaimed returned context instead of repopulating sessions.
5. Close owned contexts before awaiting active-run completion, so in-flight browser operations can finish/fail rather than deadlocking cleanup. `IsRunning` is reset only by run-finalization, never prematurely by close.
6. Only after the actual active run task finishes may the barrier complete and admissions resume. Disposal permanently disables admission before entering this barrier.

A context is never forgotten merely because its tracked page closed. Session replacement first closes the old context. Failed closes retain ownership, including unclaimed launch results, fail the close task with a fixed public exception, and keep run/Teach admissions blocked until a successful retry. **A caller must not seal profile directories after a failed close.**

The UI/vault owner must set its own lock-in-progress admission guard **before** awaiting close, keep it set throughout sealing and vault lock, and reject run/Teach requests while the vault is locked. A successful close makes reopening possible; by itself it cannot serialize a later, external `ProfileVaultService.SealAll` call against arbitrary future callers.

Do not block the UI thread with `.GetAwaiter().GetResult()` on this barrier. The base `MainWindow.SecureVaultLock` currently does so and catches close failure before continuing to `SealAll`; the parent integration must convert this to an awaited guarded lock path and stop sealing on failure. Its timeout/manual/unlock/master-password-change callers must propagate the asynchronous operation. Likewise, `App` exception handlers' direct `SealAll` calls are not engine barriers and must not race live browser profiles. These caller files are outside this engine patch's ownership.

## Cancellation limits

Cancellation is cooperative at dispatch boundaries, numeric waits, login delays, readiness and approval waits. A cancelled workflow never intentionally dispatches a later action or destination. Username-continuation and submit clicks also recheck cancellation after credential insertion/readiness.

Playwright's dispatched browser operations, especially secret-bearing `ElementHandle.FillAsync` and recorded `Locator.FillAsync`, have no atomic cancellation-token API. They are **awaited**, never abandoned with `WaitAsync(ct)`. Caller cancellation can therefore wait for an already dispatched action to finish or time out; a dispatched fill may still insert its value after cancellation. Closing owned contexts can terminate browser work, but cannot roll back a browser action already performed. External default-browser open-only windows are not engine-owned contexts and cannot be closed by this barrier.

The credential document binding does not defend against malicious scripts already executing in an approved origin, and does not make subsequent recorded clicks/fills document-bound.

## Parent integration points: enforce policy here

These are mandatory enforcement points to wire when the parent adds its policy API; this patch does not claim policy enforcement:

- `RunAsync` admission: automation-disabled, vault unlocked, lock-in-progress, and any global execution restrictions, including `includeDisabled`/selected runs.
- `RunOneAsync`: validate/authorize start and destination before creating a profile or invoking the **default-browser `Process.Start`** path. Authorize the actual reused/current URL and redirected URL after initial navigation.
- `GetPageForRecorderAsync`: automation-disabled, Teach-disabled, vault/lock-in-progress, configured start URL **and actual reused/redirected page origin**. Ongoing recorder navigation policy is also the recorder owner's responsibility.
- `TryAutoLoginAsync`, immediately before `_vault.Read`: credential-fill policy must prevent even reading stored credentials when disabled. Recheck after username continuation and other asynchronous page transitions.
- `FillCredentialAsync`: integrate credential-origin policy into the existing `VerifyAsync`/approval chain for the actual bound field's **owning-frame document origin**, including immediately before `element.FillAsync`. Do not replace the helper with a top-level `page.Url` check or a live locator, and do not put `WaitAsync(ct)` around the fill.
- The `RunOneAsync` recorded-step loop: authorize current actual URL before/after each action; block `Fill` steps when fill is disabled; authorize every `Navigate` target before dispatch and its resulting URL. Authorize the final destination immediately before navigation and its resulting URL. Initial/default-browser authorization alone is insufficient.
- `PositionWindowAsync(page, workflow.MonitorIndex)`: parent may add the stable monitor-device-name parameter and corresponding runtime stub/API. The current patch intentionally retains the available index-based API and privacy-safe fixed placement failure log.

Allowlist checks at these points are not network-level redirect interception or a browser sandbox. If policy promises those stronger restrictions, implement and test them separately.

## Verification and retained evidence

Evidence is local at `/root/projects/launchflow-evidence/both/engine`, outside the public repository. SDK: `/root/.cache/launchflow/dotnet/dotnet`; browser cache: `/root/.cache/launchflow/recorder-browsers`.

- `01`–`10`, `13`, `14` logs retain actual failing tests and subsequent passing runs for structured attention, status events, unsupported steps, privacy, exact origin, credential-free SSO, close exclusion/linked cancellation, late run/Teach launch cleanup, run-task registration, retained failed-close ownership, cancellation before username continuation, and closed-page context ownership.
- `11-unwinding-first-green.log` honestly records first-run passing additional coverage: contexts close while a deliberately blocked status subscriber still holds active-run termination; the barrier does not return early. This is a labelled inert synchronous UI-subscriber seam, not native WPF execution.
- `12-dispatched-fill-first-green.log` is compatibility coverage, not a new production RED. Its real disabled-field browser fixture demonstrates retained ownership of dispatched fills. `12-dispatched-fill-mutation-red.log` rejects a disposable source override adding `WaitAsync(ct)` around action execution. No production file was replaced by that mutation.
- The first full run (`runtime-green-initial.log`) encountered a new fixture's overly short inherited 500 ms navigation deadline while crossbuilding concurrently; the corrected fixture has a separate 15000 ms navigation timeout and 2500 ms action timeout. This is retained as a harness failure, not a product RED. The preserved document-bound fixture was not modified.
- Full runtime verification also runs the original 27 document-bound browser cases and original profile/CLI/login regressions. Separate profile tests use simulated DPAPI; application crossbuild uses warnings as errors.

```sh
export PLAYWRIGHT_BROWSERS_PATH=/root/.cache/launchflow/recorder-browsers
/root/.cache/launchflow/dotnet/dotnet run --project tests/runtime/RuntimeTests.csproj -warnaserror
/root/.cache/launchflow/dotnet/dotnet run --project tests/profiles/ProfileTests.csproj -warnaserror
/root/.cache/launchflow/dotnet/dotnet build LaunchFlow/LaunchFlow.csproj -p:EnableWindowsTargeting=true -p:PublishReadyToRun=false -warnaserror
```

### Resumed final verification

The retry resumed the saved implementation without production or fixture changes. Fresh evidence preserves all earlier logs:

- `retry-01-runtime.log`: exit 0; 148 passing assertions excluding 27 case markers, including all 27 original document-bound cases / 97 assertions and 36 engine/action/lifecycle checks. The complete runtime run also retains the original login, profile-lock, CLI and failed-restore regressions.
- `retry-01-profiles.log`: exit 0; 10 passing assertions using the simulated-DPAPI contract.
- `retry-01-crossbuild.log`: actual application **Rebuild**, exit 0, 0 warnings and 0 errors. The final command adds `-t:Rebuild` to the crossbuild command above to force recompilation rather than rely on an incremental result.
- `retry-01-preservation.log` and `retry-02-final-preservation.log`: all 101 non-owned tracked files byte-identical to starting `4faa258`; the change set is exactly the eight owned engine/runtime/handoff paths. The document-bound fixture retains the SHA-256 above. The entire `FillCredentialAsync` method region is byte-identical to both starting `4faa258` and `ae1373b`, SHA-256 `0b46b0aedf9eb6bc49bccc0d70178fdcd2a88cb3d946da2abaed4aecdcc81cd9`. Neither credential nor recorded fills are cancellation-abandoned. Added-line security scans and `git diff --check` pass. The final preservation log records the frozen owned-file hashes after this handoff update.

Exact final application command:

```sh
/root/.cache/launchflow/dotnet/dotnet build LaunchFlow/LaunchFlow.csproj -t:Rebuild -p:EnableWindowsTargeting=true -p:PublishReadyToRun=false -warnaserror
```

Parent integration remains gated on independent review of these committed bytes, resolving policy/monitor API wiring at the enforcement points above, the awaited fail-closed vault-lock/sealing caller changes, and rerunning the combined tree's runtime/profile/application plus recorder/policy/UI compatibility gates. This standalone engine result does not verify those later integrations. Native Windows execution remains a separate unfulfilled gate; no push or release was performed.

The routed fixtures exercise real Playwright Chromium actions. Lifecycle tests explicitly label inert interface seams that delay context-close or persistent-launch return; real native/headful launch timing is not claimed. Vault and monitor services in the portable harness are stubs. Linux/browser verification and a warning-free Windows crossbuild do not establish native WPF, DPAPI, installed Edge/Chrome, Windows lock/unlock, profile sealing under those browsers, or installer/runtime behavior. Native Windows verification remains required. No independent agent reviewer was spawned because this delegated task explicitly forbids additional agents; the parent must independently review the committed bytes before integration.
