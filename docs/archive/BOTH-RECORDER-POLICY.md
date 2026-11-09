# Recorder policy integration

## Enforced contract

This bounded change builds on `4d8faf1`. It consumes the actual immutable `EnterprisePolicy.Current` snapshots described in [BOTH-POLICY-INTEGRATION.md](BOTH-POLICY-INTEGRATION.md); it does not replace the policy service, engine, workflow identity/model, or parent MainWindow gates.

- `TeachRecorder(IPage)` refuses construction before registering a session when `DisableTeachMode`, `DisableAutomation`, or the actual page URL is denied. The fixed exception is `Teach recording is disabled by your organization.` The window constructs this recorder before initializing/starting capture. The parent must catch/explain constructor refusal at its own Teach entry point.
- `SynchronizeAsync`, `Resume`, `IsCapturing`, and `Drain` reevaluate policy. Native browser-binding acceptance also checks a fresh snapshot against the session's actual current page URL **and** the binding source's actual page and owning-frame URLs. It never trusts an event-supplied URL or a configured workflow start URL.
- Only the original page's main frame can enqueue a selector. Popup/other-tab/frame/shadow limitations and their existing unsupported-target observation remain intact. An otherwise unsupported target on a disallowed URL also denies the session; policy status takes precedence in the window.
- Accepted automatic clicks retain their source URLs in session memory, including after `Drain`. A changed host list therefore revokes pending **and already displayed** clicks from formerly allowed hosts, even if the browser is now on an allowed different host. Provenance is not added to persisted workflow fields.
- `CheckPolicy(params string[] additionalUrls)` is the recorder/window integration seam: current page, accepted click provenance, and supplied explicit destinations must all be allowed, and both Teach/automation flags must permit the operation. Denial pauses the session, advances the capture epoch, and clears unsafe pending state/provenance. Denial remains latched until an explicit, currently authorized `Resume`; merely restoring policy never resurrects queued clicks.
- The modeless controller checks startup before/after synchronization, timer drain/output, manual editor entry/result acceptance, current-page URL capture, removal/clear, and Save. It revalidates every displayed Navigate destination. A blocked edited Navigate cannot enter the list, and a previously allowed Navigate cannot survive a later host restriction at Save.
- Controller denial clears **all unsaved steps** conservatively and presents the fixed status `Teach recording is disabled by your organization. Unsaved steps were cleared.` It does not silently save a partial subset. Retry/start/toggle diagnostics use fixed strings rather than exception messages, selectors, or URLs. Policy refusal does not escape async event handlers as an uncontrolled exception.
- Cancel/Close remain unconditional and terminal. Already completed Saved snapshots remain terminal and are not retroactively modified. Under unchanged policy, closing the original page still preserves previously admitted clicks for Save; empty Save, pause epochs, overlapping sessions and session collection retain their existing behavior.

The original one-binding/one-init-script context bridge, serialized initialization, revisions/epochs, synchronous session detachment, structural-selector validation/privacy, and completed-click (not pointerdown) admission are unchanged. `TeachRecorderScript.cs` is unchanged.

## Executable verification

```sh
DOTNET=/root/.cache/launchflow/dotnet/dotnet bash tests/recorder/run.sh
DOTNET_CLI_HOME=/root/.cache/launchflow/dotnet-home \
NUGET_PACKAGES=/root/.cache/launchflow/nuget \
/root/.cache/launchflow/dotnet/dotnet build LaunchFlow/LaunchFlow.csproj \
  -p:EnableWindowsTargeting=true -warnaserror
```

The portable harness links production recorder, injected script, window controller, **EnterprisePolicy.cs**, engine and models; only native UI/credential/profile/monitor seams are stubbed. The Playwright instance is created inside the actual policy service's async-local test-provider scope. Its provider reads a replaceable immutable raw-value dictionary so Node-originated binding callbacks observe live test-policy changes. No machine registry values, real credentials, external websites or inference providers are used.

Default run: **93 passing checks: all 58 existing recorder checks plus 35 policy checks.** Coverage includes:

- Teach/automation disabled before recorder/window construction; disallowed actual construction host;
- flags and allowed hosts changed after capture, before Drain, before Save, and after UI drain;
- flags denied at the native callback then restored before Drain;
- disallowed actual navigated page and disallowed owning frame; allowed native capture;
- original-host provenance after draining and navigating to a newly allowed different host;
- policy changed between window construction/startup and during modal manual editing;
- fixed startup/toggle/clear denial status, blocked explicit Navigate, changed-policy Navigate Save, and denied latest-tab URL capture;
- allowed manual Navigate Save and unaffected cancellation;
- the complete preexisting completed-click/privacy/target/lifetime/weak-reference suite and saved-window click replay through production `ExecuteStepAsync`, asserting the intended element fires and its neighbor does not.

### Fixture adaptation, not skipped coverage

Every production capture fixture formerly using `data:` navigation now navigates a unique locally routed `https://recorder.test/fixture/...` document with the same HTML. The child-frame fixture uses a routed HTTP(S) child document instead of `srcdoc`, preserving dynamic frame instrumentation, unsupported-target rejection, and subsequent main-frame capture. New-page construction fixtures first navigate to an allowed routed page, because `about:blank` is not a valid policy-approved HTTP(S) host. There is no production test bypass or weakened URL rule.

All **48 original assertion call bodies** (including the optional historical-script characterization) are byte-identical to `4d8faf1`; the default run still executes all 58 original behavior checks. No assertions were removed or conditional skips introduced. The optional legacy-script mode alone still uses SetContent to characterize the old script; it does not use the production recorder/policy admission path.

### Retained evidence

`/root/projects/launchflow-evidence/both/final-recorder` contains:

- `01-construction-RED.log` / `02-construction-GREEN.log`;
- `03-http-fixtures-characterization.log` (all original behavior checks on routed fixtures);
- `04-drain-RED.log` / `05-drain-GREEN.log`;
- `06-native-RED.log` / `07-native-GREEN.log`;
- `08-window-RED.log` / `09-window-GREEN.log`;
- `10-full-GREEN.log` (93 checks), `11-crossbuild.log` (zero warnings/errors);
- `fixture-audit.py` / `12-fixture-audit.log`, comparing all original assertions and counting executed checks.

RED logs contain real failing behavior assertions, not missing dependencies or compile errors. Existing-behavior preservation and HTTP fixture characterization are first-run GREEN, not invented RED.

## Integration and limitations

- The original recorder slice did not edit engine, MainWindow/App, policy-service or native MonitorService. In the combined parent, the harness now links the real MonitorService resolver and an explicit native Screen enumeration seam to compile the engine's live saved-device resolution. This is not native monitor-placement evidence. Parent reran the full recorder suite against the combined engine and UI source successfully.
- Production engine replay here compiles the actual engine in the `4d8faf1`-based worktree, not a fake executor. The parent must rerun this harness after integrating its independent engine/monitor/UI changes. These results do not preapprove mutable sibling sources or their final API contract.
- Registry reads and dispatch are cooperative snapshots, not an atomic transaction with administrators/browser navigation. A restriction changed and restored entirely between checks is not an observable revocation. Already returned/saved output cannot be recalled. The actual current source URLs are checked at native acceptance, but hostile same-origin scripts can still fabricate structurally valid binding events; the binding is not a security sandbox against code already executing in the page/application.
- Explicit manual selectors/values and user-requested allowed Navigate URLs remain user-authored workflow data. Policy does not detect secrets in that data. Automatic capture remains structural only.
- Full popup/frame/shadow replay, arbitrary document.open/SetContent listener repair, DOM-stale selectors, full engine RunAsync/credential flows and native window positioning are not claimed by this harness.
- Native Windows registry authority, WPF rendering/event ordering/modeless ownership/timers/dialogs, and installer runtime remain separate required gates. Linux Chromium/controller execution and Windows-targeted cross-compilation are not native Windows verification.
- No additional agents were started. Frozen-commit independent review and integrated-parent gates remain the parent's responsibility.
