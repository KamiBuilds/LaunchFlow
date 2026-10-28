# LaunchFlow — ChatGPT ↔ Hermes engineering handoff

**Branch:** `chatgpt-next`  
**Base:** `both-3` at `25df42a7e2ce844b47a716891e214283ff4c1983`  
**Hermes's separate branch:** `hermes-next`  
**Application version:** `0.7.24` development preview (no release authorized)  
**Integration status:** Development changes; native interactive Windows verification still required.

## 1. Division of ownership

ChatGPT owns the **workspace user experience, selection semantics, saving and presentation integration** on `chatgpt-next`.

Hermes owns the **Windows lifecycle and credential/profile reliability stabilization** on `hermes-next`, subject to discussion of scope before touching shared UI logic.

Work on independent branches. Do not write directly to `both`, `both-2`, `both-3` or the other developer's branch. Use reviewed pull requests; avoid blanket merging two feature branches when both touch `MainWindow.xaml.cs`.

## 2. Changes on chatgpt-next

- `LaunchFlow/Services/WorkspaceCatalog.cs` is a small, WPF-independent boundary for workspace lookup, saved-selection identity and persistence with rollback.
- `LaunchFlow/MainWindow.xaml.cs` uses that boundary for Save/Preview/Run. A failed persistence callback restores the original profile name and workflow IDs, or removes an unsaved new profile.
- Changing `WorkspaceNameBox` to a name different from the selected profile **clears the old ComboBox selection**. This prevents launching or previewing a different workspace than the name displayed.
- `UpdateRunButtons` enables workspace-specific Run and Preview only when a saved profile is truly selected and the normal automation/vault lifecycle allows it.
- `LaunchFlow/MainWindow.xaml` exposes a named Preview button and a name-edit event handler.
- `tests/workspaces/WorkspaceCatalogTests.cs` tests successful saves, failure rollback, identity, invalid names, stale selection and existing-profile updates against the linked production service.
- `tests/ui/verify_ui.py` asserts the new event/control contracts.
- `tests/workspaces/{WorkspaceTests,UiTests}.csproj` compiles the production catalog, not an imitation.
- `.github/workflows/windows-ui-build.yml` now validates both linked workspace/UI suites, WPF compilation and installer packaging on `chatgpt-next` pushes.

**Compatibility contract:** No changes to the encrypted SQLite/vault schema, browser-profile envelopes, workflow IDs, WorkspaceProfile JSON, DPAPI key wrapping, policy model, installer AppId, updater feed or version.

## 2A. Independent review coordination — 27 October 2026

Hermes reported a **PASS** for `both-3` commit `851cbde5167ac03d042ab094b87759d3e1202915`, explicitly limited to the independently reviewed 14-path `ff3ef20..HEAD` delta. Reported evidence included 212 runtime checks, 109 profile assertions, 117 storage/policy assertions, independent browser/profile probes and a strict Windows cross-build. This is **not** native Windows certification, release approval or a review of all subsequent branch commits. Later `both-3` commits require their own exact-SHA gates.

A separate independent review **failed** the `hermes-next` workspace candidate for stale-object rollback after an encrypted import occurred while a workspace overwrite confirmation was open. The two reviews must not be conflated.

**My branch now contains the corresponding preventative fix:**

- `MainWindow.xaml.cs` bumps `_workspaceRevision` when an encrypted imported configuration replaces the in-memory state, and after successful workspace saves.
- Save confirmation captures the prior revision, exact workspace object and enabled workflow IDs. After the modal dialog returns, `WorkspaceCatalog.ConfirmationStillValid` verifies all three before allowing persistence.
- A changed workspace configuration produces a non-secret "Workspace changed" message and leaves imported data untouched.
- `WorkspaceCatalogTests` reproduces an imported replacement with the *same workspace name*, a detached profile instance, changed enabled workflows, and a zero-entry import that cannot be identified from object lists alone.
- These are production service/model and portable UI contract tests. They **do not prove native WPF modal reentrancy**; test that explicitly before release.

**Requested from Hermes:** retain your independent review's failing fixture and repro; compare its exact scenario against the guard here, including post-import identity, preservation of ciphertext/configuration on failure and any nested confirmation edge. Do not claim the two branches are integrated or the failure fixed on `hermes-next` until you implement and independently validate it there, or we agree to cherry-pick a reviewed commit. Do not blanket-merge `MainWindow.xaml.cs`: both branches modify it, and `hermes-next` has extracted session management.

## 2B. Hermes follow-up — pending-run and locked-vault counterexamples

Hermes independently verified the original `045d96e` overwrite guard against its preserved SQLite/import fixture (**PASS**) but found two inherited P0 counterexamples:

1. A previously resolved saved workspace could still reach the run engine after an encrypted import occurred during its KeePass password prompt, even after the old workflow had been detached.
2. Windows vault lock during overwrite confirmation let `ConfigStore.Save` use a transient in-memory fallback while the UI reported a successful workspace save; cold encrypted state did not match.

**New changes on `chatgpt-next`:**

- `WorkspaceCatalog.PendingRunIsCurrent` validates the captured configuration revision, **reference-identity** of every pending workflow, workspace membership, current enabled workflow order and current selected-only target. Same GUIDs or names with replaced objects are not sufficient.
- `MainWindow.RunEnabledAsync` captures `_workspaceRevision` before any modal prompt; checks it after master-password prompt and again after KeePass. It additionally rechecks selected workspace identity/name and selected-only workflow identity. On mismatch, the pending run is discarded without dispatch and the transient KeePass password is cleared.
- `ConfigStore.SaveDurable` explicitly refuses locked-vault writes rather than silently retaining an in-memory copy. The workspace Save callback uses this method, and the post-confirmation authorization guard also verifies Windows/session/vault state.
- Production-linked workspace and extracted UI-method tests cover same-GUID replacement, detached workspace/run-all dispatch, enabled-selection changes and vault-denied persistence. Policy/storage tests now link the real `ConfigStore` and use an encrypted SQLite database to check lock refusal and cold-unlock durability.
- `tests/workspaces/extract-ui-methods.py` now reads/writes explicit UTF-8 so the Windows runner cannot decode WPF source as CP1252.

**Review request for Hermes:** Replay your two *original preserved failing fixtures* against this exact new branch head, including real encrypted external-open import during modal KeePass/overwrite prompts and a real vault lock (not just model-only simulation). If any assertion fails, provide the smallest independent counterexample. Verify that actual run-engine dispatch count stays zero, ciphertext and cold encrypted membership remain unchanged, and the UI never claims a successful write when only transient state exists.

The full integrated CI results and native Windows behavior must be reported separately. Do not conflate the prior `both-3` scoped PASS with this unreviewed branch.

## 2C. User feedback — local no-login dashboards, clipping and installer shutdown

The user tested a development installer on Windows and reported a false sign-in warning on a private LAN dashboard containing the word “verify”, a clipped workspace Save button, and Setup asking them to exit LaunchFlow manually.

Changes on chatgpt-next:

- The New Workflow wizard now defaults to **No login required**, with disabled credential fields until the user opts into saved credentials. It saves mode None and AutoLogin=false without writing any supplied credentials. The existing Passwords tab labels mode None clearly.
- The engine's existing None-mode guard bypasses login inspection only for that selected workflow; URL restrictions and safe step execution still apply. A new real-browser regression exercises a local-style dashboard page containing “Verification” text and a visible password field while running a permitted click without a false sign-in repair prompt.
- Short localized Save / Preview labels prevent truncated sidebar buttons. Validate at different DPI and Dutch locale.
- Inno Setup's InitializeSetup calls the installed program with --exit-for-install and waits for the original process mutex to disappear. The application's existing per-current-user pipe routes this to a coordinated shutdown: save configuration, cancel automation, revoke vault access, close browser sessions, seal profiles and exit. If cleanup fails, leave the app running and refuse setup; never taskkill.
- Existing AppMutex, CloseApplications and installer AppId remain. Older installed builds don't know --exit-for-install: the first upgrade may still require manual exit. Native Windows testing must confirm this, plus tray mode, active Playwright, failed sealing and existing data preservation.

These are new branch changes, NOT a release sign-off. Hermes should independently review the installer mutex/pipe lifecycle and the no-login real-browser regression alongside his lifecycle/profile protections. Do not blindly merge MainWindow.xaml.cs.

## 2D. Website-only UI simplification — explicit user decision

The user rejected the redundant "New / existing name" input and requested **just websites**. On chatgpt-next v0.7.22 the entire Workspace Profiles area is removed from the visible sidebar, not just the input field. The top navigation and add button now say **Websites** and **New website**, and **Run all** still runs enabled websites.

Data-loss safeguards: existing encrypted Configuration.Workspaces data is neither deleted nor migrated, and WorkspaceCatalog, workspace import/revision guards and related regression tests are unchanged. A **collapsed, non-focusable and non-hit-testable** compatibility-only LegacyWorkspacePanel retains named controls so production orchestration and source-compiled UI tests continue compiling while the public UI is simplified. No end-user workspace edit/run controls remain visible.

Please independently inspect the visual layout and confirm: zero Workspace Profiles affordances, saved website list intact, website enabled toggles and Run all functional, no change to encrypted workspace/workflow records, and no unintentional focus/keyboard navigation to hidden controls. Because the older records are no longer editable in the GUI, please flag any expected export/restore or migration UX implications rather than deleting them.

No changes to your hermes-next branch, login policy or encrypted profile format.

## 2E. v0.7.23 peer-review request: usability, recorder privacy and Windows sizing

After a colleague's usability review, ChatGPT implemented on chatgpt-next:

- Bulk select/delete/edit website and recorded actions, quick Wait +1s, and explicit wait-for-selector explanations.
- Modeless Help guide plus a sanitized, illustration-only UI example; no screenshots of live credentials or private website data.
- Save status for edited website fields and steps.
- Original main window becomes visible/active again after the modeless Teach window closes.
- Work-area-constrained maximize using WM_GETMINMAXINFO, MonitorFromWindow, GetMonitorInfo and per-monitor native pixel bounds.
- Browser recorder captures only Enter/Tab/Escape/arrow keys, never ordinary typed text. On the JS side, email/password/OTP-sensitive input controls and credential forms are excluded. On the C# boundary, a second strict allowlist validates type Press. Ordered actions are recorded without values or labels.
- Existing credential encryption, enterprise policy and profile formats unchanged. Deferred: sanitized real UI screenshots need a native Windows capture without account or host details; the help guide currently uses schematic visual examples.

**Hermes review requests:** replay real Playwright recorder privacy fixtures including password/email/OTP forms, unusual contenteditable shadow elements and redirects. Verify entering search terms never writes input values/keystrokes; Enter on a non-sensitive search field can be captured without duplicates. Verify no policy-revoked actions survive pause. Validate native WPF multiselection, foreground behavior and modeless tutorial. Test maximize on a second monitor with a taskbar, 125%-200% scaling, dock/undock and portrait mode. Confirm the Save label cannot imply persistence after an encrypted-vault lock. Flag any changed source/UI test harness issues.

Preserve existing Hermes session-generation, profile safety and encrypted compatibility changes when integrating; don't blanket-merge MainWindow.xaml.cs. **No release authorized.**

## 2F. Hermes independent P0 report on 7fc2453 and v0.7.24 repair

Hermes independently reported **CHANGES REQUESTED** against v0.7.23 at SHA 7fc2453 despite green exact-SHA CI. Findings:

1. Locked vault plus unsaved website/action edits allowed the automatic installer close to silently discard changes. In v0.7.24, installer shutdown refuses exit for dirty locked vaults, requires successful durable save while unlocked and validates state again after profile cleanup. A clean locked vault can exit. Explicit tray exit semantics have not been changed in this patch.
2. A modal Step Editor could apply its result into imported replacement workflow data. In v0.7.24, Add/Edit Page/Action capture target identity and revision, and Edit additionally requires unchanged step reference and index. Production WorkflowEditGuard is linked into maintained workspace tests, covering same-GUID imports, reordering, stale selections.
3. Recorder previously discarded navigation keys on buttons and recorded Shift+Tab as Tab. V0.7.24 suppresses **only Enter** on native buttons/links (the browser emits a Click); unmodified Tab/Escape/arrow keys are captured. Unsupported modifiers are rejected with a visible warning.
4. Shadow-root and form-associated password/OTP fields could still emit non-secret Press actions. JS now fail-closes for shadow-root event paths, non-native retargeted hosts, username/payment autocomplete, external form-associated password fields and credential-bearing forms. Native bridge continues strict Press allowlisting. This is a prohibited-context capture repair; no literal secret capture was reproduced.
5. Manual KeePass entry path now marks the app as unsaved.

**Please independently replay the original provided fixtures against exact v0.7.24 SHA:** installer locked-dirty, modal encrypted import, real browser click/Tab/Enter, Shift+Tab, closed/open shadow password+OTP, external form-associated passwords and dirty KeePass UI. Check cold encrypted SQLite, engine dispatch count, UI notifications and native WPF. Do not confuse CI PASS with independent approval. Native Windows and full integration gates remain.

## 3. Hermes: highest-priority follow-up work

### P0: lock/unlock and credential lifetime

- On Windows startup, credential automation must wait until the user unlocks the master-password vault.
- Windows lock immediately pauses runs; after 10 minutes, revoke keys and seal profiles even if still locked; short unlocks must not reprompt.
- Audit lock/unlock overlap with a Playwright fill, MFA dialog, browser shutdown, sleep/resume, hotkey and tray callbacks.
- Never mark profile sealing successful if a live browser context still owns its files. Retain ciphertext and make safe recovery observable.
- Native Windows tests are required for claims involving DPAPI, session notifications and desktop positioning.

### P0: browser-profile migration and recovery

- Retain origin/`both` legacy AES-GCM output compatibility while there is no proven migration/rollback protocol.
- Preserve LFPS2 read-only support, ciphertext on errors, and no plaintext ZIP spill.
- Design a bounded-memory successor with a version header, authenticated chunks, explicit migration and rollback, and non-destructive recovery for LFPS2 packages exceeding the current 256 MiB read budget.
- Do not silently change the default on-disk format.

### P0: regression-oriented operational reliability

- Positive login-success evidence before follow-up actions; document/frame-bound credential filling; HTTPS-only; live enterprise-policy rechecks before asynchronous browser dispatch.
- Verify cancellation and page navigation cannot retarget an authorized element.
- Preserve existing monitor identity on unplug/replug; test with real displays if available.
- Require code changes to follow failing reproducible assertions, then passing assertions without weaker acceptance criteria.

### P1: architecture cleanup

- Refactor the large `MainWindow.xaml.cs` incrementally, focusing on Windows session/vault coordination and workflow run orchestration.
- Leave the workspace handlers owned by `chatgpt-next` alone until the branches are reviewed together.
- Prefer straightforward services with explicit dependencies; document only security boundaries, compatibility traps and non-obvious concurrency decisions.

### P1: release gates

- Per user request, increment the patch version for **every user-facing test build** (currently `0.7.24`; next deliverable `0.7.25`). Keep it synchronized across project, installer, AppInfo fallback, CI/UI regression expectations, changelog and artifact names. Version bumps do not authorize a release.
- A branch commit does not trigger installed-client updates. Only a newer **published** GitHub Release is considered by the updater.
- Never publish a stable release, replace existing vault files or change the installer AppId without approval.

## 4. Test and review protocol

Each engineer should provide their **branch, base SHA, head SHA, changed files, tests executed, actual results, unresolved risks and native validation gaps**. Build badges, local synthetic fixtures and cross-compiles must not be represented as native Windows desktop acceptance.

Before merging, compare both branches against `25df42a`. If both change one method, resolve deliberately, line by line; do not rely on a blind auto-merge. Keep a copy of test vault/profile data for an install/upgrade/rollback test.

Recommended manual Windows workspace checks:

1. Create and save two profiles containing different workflow IDs.
2. Select one using the mouse, keyboard arrows and Enter; verify its name appears.
3. Change the name input without saving; verify Run/Preview are disabled for the old selection.
4. Save the new profile and verify it is selected and runs only its resolved enabled workflows.
5. Overwrite an existing profile after user confirmation; verify it retains its GUID.
6. Simulate a failed save; verify no ghost profile appears and no old IDs are lost.
7. Import a workspace list and confirm stale selected objects cannot be launched.
8. Repeat with 125–200% DPI and after display disconnect/reconnect.

**No secret credentials or production browser profiles should be used in development validation.**

## 5. Requested handoff back to ChatGPT

Hermes: when your work is ready, send the user one complete message covering your current branch and SHA, decisions, errors, tests and anything you want ChatGPT to scrutinize. The user will paste it into this conversation; we will coordinate integration through their approval. Challenging each other's assumptions with evidence is encouraged.

**Current release verdict:** neither branch is automatically release-approved. A green Windows build is a prerequisite, not proof of real lock/unlock, keyboard usability, encrypted-profile recovery or signed installer upgrade behavior.
