# Native Windows 11 acceptance — stabilization candidate

Status: **NOT EXECUTED**. Portable integration tests and Windows cross-builds do not
satisfy this checklist. Record operator, Windows build, architecture, installed browser
versions, candidate full SHA, artifact hashes, test time and outcome for every case.
A blank result is unverified, not a pass. No stable release or version bump is authorized.

## Safe fixtures and installation

- Use a disposable Windows 11 VM/PC snapshot and an ordinary test account. Never use
  production credentials, browser sessions or the only copy of user data.
- On the baseline v0.7.20 application, create synthetic credentials, multiple enabled
  and disabled workflows, named workspaces, non-default settings and profile cookies.
  Record workflow/workspace IDs, values and encrypted-file hashes without logging secrets.
- Back up the complete encrypted test data and VM snapshot before each destructive probe.
  Generate profile fixtures on the same Windows account used for the upgrade: the profile
  key is DPAPI CurrentUser-bound. Copying a wrapped key to another account/machine is not
  a valid positive compatibility test. Test wrong-account failure separately.
- Use exact-SHA CI development artifacts, verifying their checksums and unsigned/development
  status. Do not redirect the stable download or publish a GitHub Release for this test.
- Test clean install with no developer SDK/runtime, then in-place upgrade from the baseline.
  Confirm the installer AppId, data location, IDs, settings and credentials remain unchanged.
  Cancel an upgrade and test normal uninstall/reinstall using only disposable data; record
  what the installer actually preserves. Native Inno execution, not parser tests, is required.

## Lifecycle and concurrency

Use an owned HTTPS synthetic sign-in site with counters for credential input, continuation,
submit and non-idempotent follow-up. Retain event/counter evidence; never use real logins.

- Fresh startup requires the master password before any credential automation, including
  RunOnAppStart, tray activation, hotkeys and a second application launch.
- Start an actionability wait and origin-approval dialog; lock Windows. No new credential
  dispatch, continuation or follow-up may start from the canceled workflow. An already
  dispatched Playwright operation cannot be atomically recalled; record that boundary.
- Unlock before the configured threshold (default ten minutes). The vault remains unlocked,
  canceled work does not automatically resume, and a configured RunOnUnlock starts at most
  one new run. Repeat lock/unlock and hotkeys rapidly; observe one owned browser session.
- Leave Windows locked and awake beyond ten minutes. Verify key revocation, browser closure
  before profile sealing, and a master-password requirement on return. Also test a shortened
  supported configuration to accelerate repetitions, but retain a default-ten-minute run.
- Delay browser closure, then perform two lock/unlock cycles before cleanup finishes. Only
  the newest unlock may prompt or run. Repeat with a password dialog and with an unlock
  delay whose timer expired while the UI dispatcher was busy.
- Suspend/resume before and after the threshold, both already locked and initially unlocked.
  An asleep OS cannot execute this application's timer. For a locked session, expiry must be
  enforced on the next available execution before credentials resume. Record event ordering;
  do not claim sealing occurred while the CPU/dispatcher was suspended.
- Crash the owned browser during a run and during cleanup. Cleanup failure must warn, retain
  profiles and block automation until an explicit successful retry. No duplicate sessions.
- Exercise the one-hour password-manager authorization timeout: fields clear and a fresh
  prompt protects the password tab. This is distinct from Windows-lock vault expiry.
- Test Exit, normal window close/background choice, OS sign-out/shutdown and installer-driven
  application closure while a browser action is pending. Verify the close/seal barrier really
  completes before process termination. `OnClosed` asynchronous cleanup is a native lifecycle
  risk to investigate, not established by portable method-body tests.

## Profile integrity and recovery

- Round-trip baseline legacy AES-GCM profiles through the candidate and back through the
  baseline reader on the same account, checking exact synthetic contents and persisted key.
- Try missing/wrong key, altered ciphertext/tag, truncated LFPS2, missing terminator, trailing
  bytes, reordered chunks, authenticated traversal and partial extraction failures. Verify
  source ciphertext hashes unchanged, no published partial target, no `.restore-*` leftovers
  after ordinary handled failure, and unaffected healthy profiles still restore.
- Test disk-full/permission-denied publication and abrupt process termination during seal and
  restore. Preserve every source package; distinguish an abandoned staging directory after
  forced termination from successful handled-failure cleanup. Retry only from backed-up data.
- Verify LFPS2 packages above the documented budget are rejected with ciphertext retained;
  do not edit limits, delete ciphertext or launch an empty replacement profile as recovery.
- Real DPAPI, Windows filesystem sharing/rename semantics and crash recovery must be observed
  here; Linux simulated DPAPI tests are not a substitute.

## Workspace and presentation

For mouse and keyboard independently, test creating a workspace, saving enabled workflows,
opening the saved-workspace dropdown, selecting another entry, editing/saving, canceling
replacement, Preview and Run. Confirm only enabled resolved saved IDs execute, in saved order.
Do not infer selection from rendered text alone: observe SelectedItem/visible name and the
actual run's website set. Verify failed persistence does not report a successful save.

- Tab/Shift+Tab focus, Alt+Down/open, arrow-key movement, Enter selection, Escape cancellation,
  popup item hit testing, click outside, main-window movement and popup closing.
- Import and restore saved workspaces, missing/disabled workflow IDs, selected-workspace
  persistence after normal restart, renamed profiles and an unavailable saved selection.
- 100%, 125%, 150% and 200% scaling; mixed-DPI displays; keyboard focus indicators; long names;
  readable contrast and no clipped Save/Preview/Run controls.
- Six displays where available; reorder, disconnect and reconnect the saved display, change
  primary monitor and reopen Preview/Run. Resolve current device identity and use the primary
  fallback without silently overwriting the saved preference.

## Release decision

Attach native evidence separately from exact-SHA CI logs. Any unexecuted or failed item remains
an explicit blocker. After independent review and authorization only, align version/assembly/UI,
Inno version and AppId-preserving upgrade, asset filenames, changelog and tag for v0.7.21.
The universal installer must continue selecting published stable GitHub Releases, not branches
or drafts. This checklist makes no compliance, accreditation or government-readiness claim.
