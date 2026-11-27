# v0.8.10 — Save-status visibility, update version and native release gates (owner-test candidate)

- New version number across WPF assembly, Inno Setup, AppInfo, README and safe ZIP generation; upgrade from v0.8.9 is no longer presented as a same-version repair.
- In Settings → Passwords, keep genuine application save errors, pending edits and startup-registration warnings visible while suppressing only the misleading green "All changes saved" status. Offer "Retry settings save" or "Save pending settings" as appropriate, separate from the credential-specific Save action.
- Run exact-SHA native Windows vault acceptance, full Windows installer/regression workflow, and a streamlined owner-test EXE builder for this branch. No production release is implied by an owner-test artifact.
- Extend structural UI regression checks for failure visibility, context-aware retry controls and new version metadata. No vault encryption, KDF, saved credential or browser-runtime behavior changed.

# v0.8.9 — Settings alignment and compact-window polish

- Preserve the intended alignment hierarchy: headings and form descriptions left aligned, workflow actions centered, and window controls right aligned.
- Make the Settings save bar contextual. Passwords now indicate that credentials need their own explicit save, with general Save now and the unrelated save status hidden; on other pages the action changes to Retry save after an unsuccessful save.
- Truncate only the live save status when space is limited, with its full text available as a tooltip.
- Make the Startup panel flexible up to 640px and wrap its lengthy Task Scheduler label, removing an unnecessary nested scroll viewer.
- Extend structural checks for tab-specific saving, retry visibility and compact-width wrapping.

# v0.8.9 — Contextual Settings UI cleanup (unreleased)

- Hide website-only Add, Run/Stop and editor toolbar while Settings is selected; the custom title bar shrinks to one row instead of leaving dead vertical space.
- Keep Getting started and About in the normal title bar on both pages, including draggable-chrome hit-test overrides.
- Preserve application save status and immediate Save now/retry in an independent compact Settings strip on every settings category. Saved credentials still require their explicit Save credentials operation.
- Group the General page into concise language and saving cards, remove duplicate headings and respect text wrapping and keyboard access.
- Extend XAML structural tests to verify contextual actions, stable title-bar help, and visible settings save feedback.

# v0.8.9 — Non-mutating inspection repair candidate (unreleased)

- Repair the confirmed cold-unlock inspection side effect without ignoring or deleting live SQLite companions. Existing vault formats, derivation parameters, ownership and recovery boundaries remain compatibility requirements.
- Retain the strict whole-directory preservation checks. Acceptance requires newly executed regression tests, independent final-source review and the full exact-source Windows matrix; earlier 0.8.8 passes do not certify this candidate.
- Remove the completed one-off observation workflow; retain its source-bound, secret-safe evidence outside the application tree. No merge or release is authorized.

# v0.8.8 — Review repair candidate (unreleased)

- Continue from the exact v0.8.7 review base `6258e8d45254216d26097ca5fe6866a06057b90e` without replacing its newer recovery or password-popup repairs.
- Assess padding on a separate inspection-only view, reject incomplete repeating patterns, and classify numeric/common-prefix suffixes by Unicode scalar. Keep exact submitted strings for confirmation, KDF and storage; preserve old vault/backup decoding.
- Route installer/build exits through startup cancellation or the real main window's existing durable-save/cleanup/refusal gate, including constructed-but-unloaded reset shells. Prevent stale consent and late main construction.
- Distinguish already-closed reset shells from live cleanup owners, and check cancellation before opening a SQLite fallback picker after either confirmation answer.
- Add mandatory no-instance/setup/unlock/recovery exit controls inside the existing six native cases. Keep exact synthetic root/WAL/SHM preservation, primary/helper exit codes, final payload checks and zero residual ownership.
- Give pending-exit phases their full supervisor budget, preserve initially absent-root semantics, and distinguish application preservation mismatches from harness failures.
- Retain 29 production-linked admission/storage/legacy regressions, including supplementary-scalar repetition, and the source-linked startup controls. Exact-SHA integrated and native Windows gates remain required before distribution; predecessor Windows results do not certify this candidate.
- Preserve encrypted formats, KDF parameters, data paths and installer AppId. No protected-branch merge or release is authorized.

# v0.8.7 — Actionable password rejection popup (development branch)

- Show a clear, reason-specific warning when a newly created master or backup password is too weak; do not silently refuse or close the prompt.
- Keep the entered password available for correction and focus its field on rejection.
- Apply the same new-password popup to first-run setup, password changes, portable exports, and recovery setup. Existing-vault unlock, old-password confirmation, and backup import are NOT revalidated.
- Add actual production-handler regression coverage for setup and standalone password prompts, including the warning, correction, and legacy unlock behavior.

## Vault and native test stabilization (unreleased)

- Integrated the independent first-run staging and cold-unlock safeguards with a single new-password acceptance policy for setup, rotation and new portable backups.
- Reject short, whitespace-only, repeated and common word-plus-number passwords; preserve decrypt/unlock compatibility for existing vaults and exports.
- Preserve interrupted creation directories and orphaned SQLite companion files instead of treating them as a new empty vault.
- Retain the paced Unicode keyboard input, wrong-password retry and window-classification repairs from the v0.8.6 native branch.
- Fixed Windows PowerShell 5.1 package ZIP entry separators and added/maintained native and portable regression coverage.
- Only exact-SHA CI and real Windows installed-product acceptance can authorize a release.

# v0.8.5 — Vault reliability integration (unreleased)

- Bring first-run encrypted staging, no-clobber publication, diagnostics and recovery from the reliability candidate together with coherent new-password acceptance and export password rules.
- Display green only when the same validator that creates the vault would accept the input. Reject short, whitespace-only, simple repeated and common word + numeric passwords (including test01020345 / welkom010203) for NEW master/export passwords.
- Preserve all old vault and backup unlock/decryption, format/KDF parameters and user data without revalidation on unlock.
- Explicitly disclose a retained interrupted creation stage instead of routing it through a broken live-vault recovery operation or silently treating it as a clean install.
- Use a unique 0.8.5 version across application, setup and safe source/portable ZIP.
- Address Windows PowerShell 5.1 source ZIP entry-name parity and native ledger serialization. Full same-commit Windows native acceptance must pass before release.
- Repo branch clean-up is separate from this commit and must not alter protected refs or discard unmerged work.

# v0.8.4 — First-run vault reliability candidate

- Enforce one shared password eligibility result so setup cannot approve input rejected by storage; keep valid-password representation and existing minimum rules unchanged.
- Stage new encrypted vault creation, complete/checkpoint/dispose SQLite resources before non-clobbering publication, distinguish uncertain outcomes, and preserve existing or retained material. Pre-commit failures no longer leave a blocking final-path empty database.
- Add explicit preserved fresh-start recovery with separate confirmation, recognized-ownership refusal, rollback protection and a startup-blocking preservation-intent marker.
- Add bounded secret-safe creation phases, admission evidence and exception graphs; blocked diagnostics remain best effort. Zero owned temporary password/configuration byte buffers.
- Track final verification and remaining native/review gates in `docs/vault-first-run-reliability.md`.
- Existing encrypted-vault format, derivation rules, data locations and installer AppId remain compatibility boundaries.
- The reported valid-password VMware startup failure is not yet reproduced or resolved; this candidate is not a stable public release.

# v0.8.3 — Short-path Windows installer packaging (includes v0.8.2)

- Make privacy/packaging path checks compatible with Windows PowerShell 5.1 (the interpreter launched by BUILD-INSTALLER.cmd); the previous Path.GetRelativePath call only worked under newer .NET/PowerShell.
- Add PowerShell 5.1 packaging regressions and run the real installer build from that shell in CI to prevent recurring VMware-only builder failures.
- Fix Inno Setup MAX_PATH failures for Playwright Firefox files under deeply nested extracted source folders by staging a verified payload in a short temporary directory.
- Include hidden Playwright driver files and bundled Firefox automation runtime, revalidate staging, then clean up on success/failure.
- No locally installed Chrome, Edge or Firefox is required to build. Runtime browser discovery remains unchanged.
- Retain v0.8.2 independent per-user master-password/vault initialization and distribution-secret rejection.
- Native Windows installer and first-run acceptance tests remain necessary before distributing a stable release.

# v0.8.2 — Critical fresh-install vault isolation and distribution safety

- First-run setup cold-reopens the newly created encrypted vault using the exact master password chosen by that user; failed verification preserves the vault without reset.
- Distributed LaunchFlow ignores inherited `LAUNCHFLOW_TEST_VAULT_PATH` overrides, enforcing the current Windows user's local vault directory.
- Packaging now fails closed if published binaries or browser payloads contain a local vault, encrypted backup, KeePass database, recovery data, browser session profiles or other user secrets; installer exclusions add defense in depth.
- New independent-vault tests create two synthetic installations with distinct passwords and check cross-owner rejection, random per-vault IDs/salts, and cold reopen.
- Packaging tests inject private files and require the distribution gate to reject every fixture.
- This source change needs native Windows build and a full first-run wizard-to-main-window acceptance test before distribution.

# v0.8.1 — P2 source-pinned repair for v0.8.0 review

- AUTO080-01: complete same-snapshot challenge enumeration, including visible passwords after large/hidden input populations and open shadow roots.
- AUTO080-02: refuse keyboard press when focus is retargeted through an opaque (closed) shadow root.
- AUTO080-03: recorder synchronization no longer consumes the navigation since the last represented taught action.
- PLAT080-01: drain schtasks stdout and stderr concurrently, avoiding a redirected-pipe deadlock for large CSV output.
- HELLO080-01/02: admit Hello enable ownership before any asynchronous consent; newer enables and strict disable/reset revoke older operations before publication.
- STARTUP080-01: first-run and post-reset setup reconcile both scheduled task and Run-key registrations; disclose partial OS failures after durable vault setup.
- SCHED080-02: keep ownership of unconfirmed-live task scheduler children and refuse conflicting retries.
- TEST080-01: link the matching SQLite dependency in the profile recovery test project.
- No vault format, installer AppId, data directory or credential destination policy changes. Native Windows and real-account acceptance remain required.

# v0.8.0 — Minor milestone after ownership/reset/Hello hardening

- Version milestone from the 0.7.x rescue line after F3/reset/confirmation/Hello/scheduler finish-path repairs through 0.7.100.
- No installer AppId, vault format, or data-location change.
- Native Windows interactive acceptance remains required before any stable public release claim.

# v0.7.100 — Hello preference/secret consistency on abort paths

- Password-change: if Hello file removal fails after Hello-off was saved, roll the preference back.
- Enable Hello persist rollback uses DisableStrict and warns when unlock material remains.
- Native Windows interactive acceptance remains required.

# v0.7.99 — Finish-path hardening before native acceptance

- Clear SQLite connection pools on Lock and before vault-reset deletes (Windows file-lock).
- Aborted reset does not re-seal while browsers may still be live; post-wipe setup handoff cannot leave a headless process.
- Hello disable / password-change use DisableStrict and verify the current password before removing Hello.
- Scheduler presence uses CSV task-list name match (exit code 1 is not treated as Absent); Run-key still applies if task cleanup fails.
- Structural challenge visibility no longer rejects opacity:0 OTP overlays.
- Native Windows interactive acceptance remains required.

# v0.7.98 — Cursor failure-audit repairs (F-097-01…08)

- F-097-01/02: structural challenge snapshot pierces open Shadow DOM and uses computed visibility (not getClientRects alone).
- F-097-03: Press walks open shadowRoot.activeElement into nested iframes; unreadable focus fails closed.
- F-097-04/05: Hello stages to a temp file, publishes under a gate with disable-epoch barriers; DisableIfPublication returns Removed/Superseded/RemovalFailed and UI warns on retained secrets.
- F-097-06: scheduler query is Present|Absent|UnknownError; access/query failures no longer look like “missing task”.
- F-097-07: aborted reset (vault still present) attempts re-seal and warns if profiles may be plaintext.
- F-097-08: strict snapshot token refreshes only after SQLite commit succeeds.
- Native Windows interactive acceptance remains required.

# v0.7.97 — Review P2 ownership repairs (F3 / reset / confirmation / Teach / Hello / scheduler / Firefox)

- F3: vault writes require sealed config+credential snapshot freshness as well as key identity (same-key stale writers refuse).
- Reset: skip profile seal before wipe; disclose partial profile cleanup after DB removal; keep dirty editor ownership until wipe is terminal.
- F1: structural security challenge is one browser-side snapshot (no helper-internal await gap).
- AUTO-A4: remembered SamePage no longer vetoes owned auto-discovered Next.
- AUTO-CASE-PATH: Teach Navigate preserves case-sensitive path identity; Synchronize resets capture URL baseline.
- PARENT-PRESS-01: Press authorizes the deepest focused frame (nested iframes).
- UI96-01: Hello enable publication generation; stale cleanup cannot delete a newer enable’s file.
- PLAT-03 / PLAT96-01: scheduler children are reaped on timeout; missing-task delete uses query, not English text.
- PARENT-FIREFOX-01: readiness requires `firefox-<rev>/firefox/firefox.exe`.
- Stale ui/runtime test oracles refreshed for session-bound SaveDurable and CryptographicException profile refusal.
- Native Windows interactive acceptance remains required.

# v0.7.96 — Lean source delivery ZIP

- Version bump for a clean delivery archive that excludes `bin/`, `obj/`, `.git`, and other build outputs (prior ~779 MB zip was dominated by local build artifacts).
- Native Windows interactive acceptance remains required.

# v0.7.95 — Vault reset requires strict Hello material removal

- `VaultResetService` uses `WindowsHelloService.DisableStrict()` so a claimed wipe cannot succeed while `hello-unlock.bin` still exists.
- Runtime reset continues to setup only after Hello material is gone; otherwise shuts down with an explicit cleanup message.
- Stale in-flight Enable Hello (ownership lost after `EnableAsync`) uses `DisableStrict` so reset/lock cannot leave a recoverable secret.
- Ordinary UI `Disable()` remains best-effort.
- Native Windows interactive acceptance remains required.

# v0.7.94 — Post-wipe Close belt-and-suspenders

- `OnClosing` allows Close while `_exiting` when the vault file is already gone (covers exit-permit grant races after wipe).
- Retains v0.7.93 permit grant before Close and dirty-revoke skip when vault missing.
- Native Windows interactive acceptance remains required.

# v0.7.93 — Post-wipe Close must hold an exit permit

- After vault wipe, grant `ExitCompletionGate` before `Close()` so `OnClosing` does not cancel under bare `_exiting` and leave the old window beside setup.
- When the vault file is already gone, a permitted close no longer revokes on late dirty/editor-dialog state.
- Native Windows interactive acceptance remains required.

# v0.7.92 — Vault-reset exit cannot strand MainWindow without a vault

- Runtime reset sets `_exiting`/disables UI before drain+wipe; aborted pre-wipe paths restore `ShutdownMode` via `CancelVaultResetKeepAlive`.
- After the vault file is gone, setup continues even if profile cleanup was partial — skips dirty/save `CompleteOwnedExitAsync` gates that could leave a live window without a database.
- Native Windows interactive acceptance remains required.

# v0.7.91 — Hello confirm without session bump + deferred vault reset

- `MasterPasswordService.Verify` / `VaultDatabase.ConfirmPassword` verify while unlocked without `ZeroKey`, cache reload, or `SessionGeneration` bump (Enable Hello no longer aborts after a correct password).
- Enable Hello shows an unlock-first message when the vault is locked.
- Forgot-password reset only signals intent in `UnlockWindow`; MainWindow closes/drains browsers via `SecureVaultLockAsync` before `VaultResetService` wipe; cold-start App path still wipes immediately (no sessions).
- Master-password change aborts if Hello-off cannot be persisted before rotation (Hello material left intact).
- Native Windows interactive acceptance remains required.

# v0.7.90 — Hello coordinator vault-free + persist parity

- `HelloUnlockCoordinator` no longer reads `VaultDatabase` (fixes WorkspaceTests compile when Controllers are linked without vault stubs).
- Enable/disable preference writes mirror welcome-checklist durability (rollback/disable on save failure; disable path swallows persist failure after local disable).
- Master-password change clears Hello material/epoch and SaveDurable Hello-off **before** `ChangeMasterStrict` re-seals (post-change session bump made the earlier write unreachable).
- Native Windows interactive acceptance remains required.

# v0.7.89 — Architecture maps + Hello unlock coordinator peel

- Documented ownership contracts (revision + vault session + ReferenceEquals), MainWindow partial map, and AutomationEngine partial map under `architecture/`.
- Refreshed `architecture/overview.md` and `LaunchFlow/STRUCTURE.md` links to match the current tree.
- Extracted `HelloUnlockCoordinator` for Hello enable-epoch and durable preference writes; MainWindow keeps WPF prompts only.
- Did not split AutomationEngine into separate Playwright owners. Native Windows interactive acceptance remains required.

# v0.7.88 — Remove obsolete agent/handoff documentation

- Deleted obsolete historical agent/handoff docs under `docs/archive/` and scrubbed remaining references from ownership inventory, installer verification, recorder/workspaces READMEs, STRUCTURE, stabilization status and AGENTS continuity notes.
- Kept current product docs under `docs/` and the engineering rescue contract in `AGENTS.md`. Native Windows interactive acceptance remains required.

# v0.7.87 — Welcome checklist preference only sticks after durable save

- Mark WelcomeChecklistShown durable only when SaveDurable succeeds under the captured vault session; revert the in-memory flag on failure so first-run coaching can retry.
- Completes the last Low item from the CLEAR High/Medium runtime review. Native Windows interactive acceptance remains required.

# v0.7.86 — KeePass status label exact identity and refreshed UI extract

- KeePass selected-entry status label shows the exact path identity (no Trim).
- Regenerated workspaces UiMethods extract so seam tests match production Repair/credential bodies.
- Follow-up hygiene after CLEAR High/Medium runtime review. Native Windows interactive acceptance remains required.

# v0.7.85 — Session-bound settings writes and KeePass credential-path identity

- Workspace save, Hello enable/disable, welcome checklist and shortcut capture use SaveDurable with the captured vault session instead of session-free Save.
- Credential save preserves exact KeePass entry identity (no Trim), matching SaveAll/editor mapping.
- Follow-up to the v0.7.83/0.7.84 runtime regression review. Native Windows interactive acceptance remains required.

# v0.7.84 — Repair/Teach exclusion and delete-draft dirty continuity

- Revalidate Run/Teach/engine ownership after the Firefox readiness modal inside Repair, matching Record.
- Mark the editor dirty after restoring a refused website-delete draft so exit/autosave still see pending text.
- Keep recorder test stubs aligned with HasValuableSealedProfile.
- Candidate follow-up on 0.7.83. Native Windows interactive acceptance remains required.

# v0.7.83 — Cross-writer vault identity, profile seal admission and UI/platform P2 repairs

- Guard vault configuration/credential writes and master-password rotation with an authenticated on-disk vault identity token so independent stale instances cannot overwrite a newer or rotated vault.
- Keep confirmation structural challenge inspection last; preserve exact KeePass entry identities; apply Add-website editor after wizard await; revalidate Run/Teach exclusion after Firefox readiness; evict a still-owned failed KeePass cache on retry.
- Reauthorize recorded Fill immediately before dispatch; authorize Press against the focused frame URL; teach Navigate for Press URL transitions or refuse consuming them; do not let a remembered SamePage hint veto an explicit continue selector.
- Profile seal validates restore admission limits before deleting plaintext; refuse empty placeholders that would overwrite valuable sealed snapshots; do not publish empty first-launch seals that permanently block retry.
- Portable incomplete-vault recovery projects configuration through ForThisDevice; Hello enable uses ownership epochs; delete restores pending drafts; runtime forgot-password reset routes to setup; recorder Add-current-page refuses secret-bearing URLs.
- Publish-draft checks out LF_SOURCE_SHA before signature verification; Firefox readiness requires the package revision; scheduled-task delete failures propagate; update-check diagnostics require consent; uninstall removes Run key and LaunchFlow Autostart without wiping vault data.
- Candidate repair from aee7e0b / 0.7.82. Native Windows interactive acceptance remains required before any public release claim.

# v0.7.82 — Refuse incomplete browser-profile restore and clear failed decrypt buffers

- Fail closed when an interrupted browser-profile restore leaves a reserved `.restore-*` directory containing potentially plaintext browser data. Both Lock vault and profile restore now refuse to claim success until the user resolves the retained staging files.
- Rejecting a legacy AES-GCM profile authentication tag now explicitly zeroes the temporary decrypted buffer and returns an empty byte array.
- Reject an empty browser-profile directory beside a retained encrypted snapshot during both restore and engine launch. Do not mistake a placeholder for successfully restored cookies or overwrite the sealed original.
- Extend actual production-linked profile and filesystem cleanup tests to cover retained restore staging, refusal on both lock and unlock, and failed-decryption output cleanup.
- No automatic deletion of unverified recovery files; Windows owner acceptance remains required.

# v0.7.81 — KeePass session continuity and encrypted-backup memory safety

- Bind optional KeePassXC credential results to the original unlocked vault session; a lock/reunlock invalidates any outstanding external read.
- Recheck organization credential policy after the external KeePass lookup, so a policy change while the CLI is running cannot return a secret.
- Validate encrypted backup header and ciphertext length before expensive password derivation; reject truncation and trailing data without touching existing backups.
- Deserialize decrypted JSON directly from UTF-8 bytes and zero temporary plaintext/key/ciphertext buffers after use; zero transient export plaintext after encryption.
- Add source-linked encrypted-vault regression tests for session turnover and policy revocation, and backup-integrity tests for valid, truncated and trailing-data archives.

# v0.7.80 — Locked credential fallback, tray-save and import safety

- Guard the optional KeePassXC fallback with the LaunchFlow vault lock, including authorization recheck after CLI lookup.
- Close-to-tray uses the same durable-save guard as Exit, preventing locked dirty editor state from disappearing without warning.
- Preserve imported first-website guidance history to avoid repeating already-seen prompts.
- Reject case-insensitive, whitespace-normalized duplicate workspace names in imported backups before ambiguous workspace selection.
- Add locked-vault credential and background-close regression checks; Windows acceptance remains required.

# v0.7.79 — Portable import graph-integrity checks

- Preserve extra encrypted credential records in older backups even when their websites have been removed, as required by the legacy recovery contract.
- Password-free duplication now omits legacy Fill actions and unused typed step values; it informs users when steps need re-recording and preserves the original workflow.
- Reject null, duplicate-id, invalid-name, oversized or empty-id workspace entries before importing encrypted data.
- Preserve backward compatibility for workspaces referencing websites removed in older backups; their resolver safely ignores missing IDs.
- Add encrypted-file and in-memory regression coverage. The Windows installer and real-account login still require native acceptance before any public release.

# v0.7.78 — Linked run consent and browser-profile family safety

- Accept the engine-owned linked cancellation token for live password-page consent while rejecting UI parent tokens and stale/finished engine runs. Preserve UI run lease, editor, vault and host-policy checks.
- Refuse to launch Firefox against an existing Chromium profile (and vice versa), preserving its files and encrypted snapshot; existing compatible profiles remain usable.
- Duplicated workflows now drop inherited KeePassXC references and all origin trust grants, matching the UI promise of a copy without credentials.
- Add source-linked UI, engine lifecycle, synthetic filesystem and safe-diagnostic regressions. Native real-account login remains a separate acceptance gate.
- Candidate build: no automatic release, migration or overwrite of encrypted data.

# v0.7.77 — Approval ownership, browser switching, and privacy fixes

- Explicitly distinguish username-origin approval from a password-destination grant. The native consent dialog says what is authorized, and the engine no longer overwrites a durable approved destination after returning from a modal.
- Bind approval callbacks to the originating UI run lease and cancellation token before dispatcher scheduling; reject superseded runs and late edits.
- Close and release a previous browser context when a workflow changes its selected browser, rather than silently returning the old window.
- Bound user-facing exception explanations to fixed safe messages, including browser readiness, no leaked URL/selector/exception payload.
- Cancellation and failed-run notifications no longer claim browser actions were reverted or stored data independently verified.
- Source-linked and behavioral regression tests cover these boundaries. Native account acceptance remains separate.

# v0.7.76 — Remaining v0.7.75 boundary repairs (candidate)

- Recheck current authorization after the final awaited post-fill DOM inspection, and
  perform the compatibility model write in a single browser evaluation that
  verifies the original document, origin and form owner before each notification.
- Keep the verified synchronous final password-destination gate and secret-free
  InputEvent.data. Add real Chromium late-inspection/healthy-model regressions.
- Scope KeePass picker, cache and status to the initiating operation. Preserve
  owned automatic database discovery and autosave, suppress only self-generated
  editor TextChanged notifications, and publish the validated model before UI.
- Relocate KeePass chooser UI logic to its existing partial and exercise the
  production dirty-marking handler through event-aware portable UI fixtures.
- Add internal, narrowly scoped test-only file/root deletion collaborators so
  portable profile reset failures are meaningful on Linux. Retain Windows
  sharing-denial checks and actual production filesystem deletion.
- Extend the encrypted SQLite cold-reopen test for KeePass entry/database/key.
- **Candidate only:** real .NET, browser, WPF/native Windows, and hosted gates
  must pass on the exact final commit before release readiness can be asserted.

# v0.7.75 — Six boundary fixes: post-fill, reset honesty, Hello freshness, KeePass ownership, OTP ack, update URL

- Guard the post-FillAsync credential model sync with renewed cancellation/policy/destination/document/form checks; stop putting secrets in InputEvent.data.
- Make requested vault/profile reset report partial wipe failures instead of claiming success when profile roots remain.
- Collect typed unlock input only in the dialog; refresh Windows Hello freshness only after successful typed verification (not Hello-derived unlock).
- KeePass entry selection captures configuration/workflow/session/database/key ownership and refuses stale publication after awaits.
- Manual sign-in acknowledgment shares LoginHealth structural OTP/challenge detection so visible name=otp and labelled codes block follow-up.
- Validate update release URLs as absolute HTTPS pages before shell open; reject file/custom/user-info targets from feed metadata.
- **Candidate only:** native WPF/installer/account acceptance still required. No release authorization.

# v0.7.74 — Candidate: bounded diagnostics, consent orchestration and focused regressions

- One known-code diagnostics mapping for login failures, safe fallback for untrusted error text in tray notifications, and allowlisted phase updates. Unknown failures stay unknown.
- Extract native password-origin approval decision into a narrow, WPF-independent coordinator. The WPF adapter preserves modal consent, captures run/editor/vault/policy ownership, and uses the existing detached durable save transaction before publishing a grant.
- Source-linked approval regression cases cover stale selections/import, late editor changes, durable rejection, pending edits and lock/unlock; a Chromium protected-fill case covers Stop during pending consent and a fresh retry.
- Refresh the current ownership/source map while retaining the historical mapping. Final synchronous password destination check and installer identity remain unchanged.
- **Candidate only:** native WPF/installer/account acceptance still required. No release authorization.

# v0.7.73 — Password destination authorization boundary and installer fixture repair

- Keep first-time HTTPS IdP approval but require the exact mapped password destination synchronously immediately before password dispatch; never reopen approval after final document/form validation.
- Add maintained production-linked Chromium regressions for same-site, mapped and first approval, initial form changes, late mapping invalidation and explicit denial, observing input events as well as the final field value.
- Repair the synthetic Windows builder fixture at its own v0.7.19 version (AppInfo, README, CHANGELOG and real representative Inno mutex wait loop). Test malformed preflights without changing prior installer artifacts.
- Preserve installer AppId, encrypted vault format, profile layout and workflow IDs. This is an unsigned development candidate; CI and native Windows/real-account acceptance are required.

# v0.7.72 — Restore source gate and portable backup round-trip contract

- Update the source-validation gate for the v0.7.69 beginner UI; ensure the simple sign-in and recorder controls remain wired while the legacy advanced button remains hidden.
- Before creating or replacing a portable encrypted export, validate the exact captured vault snapshot against the same rules used for import.
- Refuse legacy Fill actions and typed recorded values with an actionable, non-secret warning. Do not silently strip steps, write an unusable backup, or replace an existing valid archive.
- Add encrypted backup round-trip and no-clobber regression tests. Vault format and installer AppId unchanged; unsigned test build.

# v0.7.71 — Import hardening + clearer click labels

- Portable import clears KeePass database/key/entry paths so another PC’s files are never assumed here.
- Import refuses Fill actions and any recorded step that carries typed values.
- Click lists always show a human label (`DisplayLabel`) even when Description is empty.
- Same-site clean path changes teach Navigate so replay follows SPA routes; query/fragment still never enter Navigate.

# v0.7.70 — Dark click list + safer import

- Empty recorded-clicks area uses a dark bordered panel with a clear hint instead of a blank white box; ListBox chrome no longer falls back to system white.
- Portable import validates decrypted packages (http(s) only, known action types, size bounds) before replacing the vault.
- Vault `PRAGMA table_info` only accepts fixed internal table names; website names with SQL metacharacters stay parameterized data.

# v0.7.69 — Simple recording for real users

- Hide Settings → Special help (CSS selectors) from normal users; remove “if IT asks” paths from the websites screen.
- Recorder shows a pulsing red RECORDING banner, keeps Select all / Delete visible (not “optional”), and no longer owns the main window so LaunchFlow stays scrollable.
- Same-site navigations that add `?query` / `#hash` no longer drop the next click; OAuth cross-site callbacks with secrets still stay unrecorded.

# v0.7.68 — Calm failure UX (no more yelling)

- Run misses use quiet status and tray notes (“Couldn't finish”, “Almost there”) — no Warning/Error balloons.
- Sign-in helper no longer yanks LaunchFlow out of the tray; small “Quick check” window only if the app is already open.
- Softer sign-in tip wording; Firefox setup dialogs use Information, not Warning.
- Vault format and AppId unchanged. Unsigned test build.
# v0.7.67 — Faster first-try sign-in, maximize, same-window reuse

- Wait for SPA sign-in boxes before failing (fixes npm/React first-click miss / second-click success).
- Broader npm signed-in detection; recognize identity/secret login fields; formless React login cards.
- Status strip shows plain phases (Opening browser…, Waiting for sign-in boxes…, Signing in…).
- Browser opens maximized on the chosen screen; re-running the same website reuses that window (no second copy).
- Vault format and AppId unchanged. Unsigned test build.
# v0.7.66 — More browsers + beginner-friendly wording

- Detect and Auto-launch Brave, Opera, Opera GX, and Vivaldi (plus Edge/Chrome) from this PC; Opera vs Opera GX stay distinct.
- Same-page email+password and email-then-password copy clarified; LaunchFlow still finds the boxes itself.
- Recorder list shows plain “Clicked something on the page” instead of technical paths; Special help / guide / Getting started rewritten for non-IT users (no CSS/Playwright jargon in the UI).
- Vault format and AppId unchanged. Unsigned test build.
# v0.7.65 — Installer exit wait + Firefox ready success path

- Setup waits up to 240s for `--exit-for-install` mutex release so browser-close + encrypted-profile seal budgets can finish (was ~30s and could abort a still-legal shutdown).
- After Browser readiness successfully installs Firefox automation, Teach/Repair/Run no longer mark Failed/cancelled before continuing.
- Native installer smoke asserts shipped `playwright-browsers/**/firefox.exe`; version gate covers CHANGELOG/README/AppInfo; inventory reads csproj version.
- Wizard copy and deployment contract match Setup-shipped Playwright Firefox (Edge/Chrome remain host-installed). Universal Installer stays a separate 1.0.0 bootstrapper identity.
- Backfilled missing 0.7.62–0.7.64 changelog notes. Vault format and AppId unchanged. Unsigned test build.

# v0.7.64 — Windows-tested packaging candidate

- Product/Setup/AppInfo/README identity set to **0.7.64** for the Windows-tested Setup artifact (commit `432cf17`).
- Carries the 0.7.61 exit/profile quarantine gates plus the 0.7.55–0.7.59 Firefox Auto / Browser readiness / installed Edge-Chrome scan stack.
- Changelog narrative for this bump was not recorded at ship time; restored here for the versioning contract.

# v0.7.63 — Intermediate Windows test-candidate rebuild

- Patch bump on the 0.7.61 stabilization surface for native Windows verification iterations.
- No separate public behavior note was retained at bump time; treat as packaging/test-candidate continuity toward 0.7.64.

# v0.7.62 — Intermediate Windows test-candidate rebuild

- Patch bump on the 0.7.61 exit/profile security stack for continued native Windows acceptance work.
- No separate public behavior note was retained at bump time; treat as packaging/test-candidate continuity toward 0.7.64.

## v0.7.61 — P1 exit and profile security stabilization (test candidate)

- Require cleanup of authenticated, workflow-associated plaintext browser-profile quarantines before reporting a successful lock or restore; retain unknown legacy quarantines for manual recovery rather than hiding them.
- Put time budgets around browser shutdown, run cancellation, final disposal and profile encryption; a timeout blocks exit and never cancels in-flight cleanup or grants a fake success.
- Veto Windows session ending when editor changes cannot be durably saved, and guard the final shutdown close path against save failures.
- Repair synthetic installer tests to exercise stale-artifact failure paths without trying to execute placeholder Playwright Node binaries; production Firefox packaging remains unchanged.
- Preserve encrypted storage layout, release identity and the existing installer AppId. No automatic release.

## v0.7.60 — Safe exit performance and shutdown visibility (test candidate)

- Skip redundant encrypted configuration writes when an exit or background hide has no unsaved edits; preserve the fail-closed dirty-vault save contract.
- Close independent tracked browser sessions concurrently, retaining failed contexts for a safe retry before encrypted-profile sealing.
- Use faster ZIP compression for browser-profile sealing without changing the encryption, key, archive layout, or publication ordering.
- Display closing phases in the main window while browser cleanup and encrypted-profile sealing run.
- Add browser-session concurrency/failure regression cases and clean-exit persistence fast-path cases. Failed remembered-login writes remain pending so a fast exit cannot discard them.
- No automatic merge or release. Native Windows acceptance still required.

# v0.7.59 — Browser readiness and Firefox cache consistency

- Resolve Firefox automation readiness and Playwright launch from the same installed browser root, including existing default `ms-playwright` caches.
- Distinguish bundled, consent-installed and cached Firefox builds in the readiness dialog; preserve stock Firefox exclusion.
- Make Browser readiness resizable with vertical scrolling for long installation paths and localized/low-resolution layouts.
- Align the in-app troubleshooting step, generated getting-started guide and **Scan again** button.
- Add synthetic browser-path priority/fallback tests and source UI regression checks; preserve vault format and installer AppId.
- Keep the portable UI regression harness linked to the newer Firefox readiness and run-failure production methods.

# v0.7.58 — Find installed Edge/Chrome/Firefox on this PC

- Scans App Paths + common folders for Edge, Chrome, and Mozilla Firefox; Auto launches the exact Edge/Chrome `.exe` found.
- Browser readiness lists each browser with Found/Not found paths; Scan again refreshes detection.
- Firefox Auto still uses the Setup-shipped Playwright Firefox (mozilla.org Firefox is detected for status only).

# v0.7.57 — Firefox Auto fully fixed (shipped automation build)

- Installer packages Playwright Firefox under `playwright-browsers/`; app points `PLAYWRIGHT_BROWSERS_PATH` at it on startup (no stock mozilla.org `firefox.exe`).
- Browser dropdown offers Firefox only when automation Firefox is present; Teach/Repair/Run share the same readiness gate.
- Consent reinstall remains in Browser readiness as a fallback. Concrete failure toast + help dialog retained from 0.7.56.

# v0.7.56 — Firefox failure shows real reason (not generic toast)

- Workflow failures keep a public reason; a single Firefox automation miss no longer collapses to “1 workflow(s) failed”.
- Pre-run check + post-fail dialog offer Browser readiness / Edge-Chrome when Firefox Auto is selected without Playwright Firefox.
- Tray balloon uses the concrete failure text for one-failure runs.

# v0.7.55 — Firefox automation + TE Vue InputEvent

- Firefox Auto no longer drives stock mozilla.org `firefox.exe` (blank window, no navigation). Browser readiness can consent-install Playwright’s Firefox, or use Edge/Chrome.
- ThousandEyes Vue bind uses `InputEvent` so Log In advances; longer wait for SPA / IdBroker password stage. Docs: region dropdown, Cisco Account cookies.
- Standing rule: each delivered change set bumps patch version by 1 (csproj + ISS + AppInfo fallback + changelog).

# v0.7.54 — IdP password continuation + Firefox launch attempt

- Email-first → other-host password (Cisco/Webex IdBroker, Okta, Entra, …) prompts once, maps Trusted password page — no product hostname hardcoding.
- Earlier Firefox stock-exe launch attempt (superseded by 0.7.55).
- Same-site ThousandEyes SPA password fixes (legacy submit selector, `#submit-button`).
- End-user README in plain language. Vault format and AppId unchanged. Unsigned test build.

# v0.7.53 — Remembered login classification + recovery/guide hardening

- Remembered `CustomSelectors` no longer disables automatic form classification while default username selectors remain (`LoginFieldDiscovery` + `LoginFieldDefaults`).
- Review probe covers registration-before-login with a remembered CustomSelectors recipe.
- Vault recovery stages and inspects the backup before quarantining live db/wal/shm; rollback restores the full set; path-explicit API for harnesses.
- Getting-started verify script follows the UI folder layout; delete/credential dialogs capture config/session/revision before confirmation.
- Development preview only; native Windows verification remains the release gate.

# v0.7.52 — Fix save publication + destructive-dialog ownership

- `SaveAll` publishes the validated draft onto the **live** configuration (no longer re-applies only onto the detached candidate).
- Website delete requires the confirmed workflow instance to still be a member of the owned config and a unique ID in the snapshot.
- Multi-step delete revalidates editor ownership and step membership after confirmation.
- Autosave/architecture regressions assert live publication. Development preview only.

# v0.7.51 — Cleanup pass: archive clutter, peel MainWindow helpers

- Archived obsolete version handoff docs (later removed from the tree; current guides stay in `docs/` + `architecture/`).
- Extracted `WindowWorkAreaConstraint`, `RunOutcomeSummary`, `PasswordSessionPolicy`, and `GlobalShortcutRegistrar` so maximize math, run tray wording, password-tab timing, and hotkey registration are not buried in MainWindow.
- Removed obsolete `tools/Verify-MaskedCredentialSave.py` (v0.7.8 UI asserts).
- Added pure architecture checks for the new helpers. Vault format and AppId unchanged. Development preview only.

# v0.7.50 — Domain folder restructure

- Physical layout: `UI/Shell`, `UI/Dialogs`, `UI/Controllers`, `Domain/Models`, and domain folders under `Services/` (Vault, Automation, Login, Teach, Persistence, Browser, Transfer, Workspaces, KeePass, Platform, Diagnostics, Policy).
- Namespaces unchanged (`LaunchFlow.Services` / `.Models` / `.Controllers`) so behavior and test type names stay stable.
- Test/tool path links and architecture checks updated; see `LaunchFlow/STRUCTURE.md`.
- Synced installer + AppInfo display fallback with csproj version.
- Preserve vault crypto format and AppId. Development preview only.

# v0.7.49 — Login recipes + clearer sign-in failures

- Remember successful Auto login shapes per website (`SamePage`, `EmailThenPassword`, `CustomSelectors`) and prefer them on later runs; Forget shape resets discovery.
- Sign-in attention messages name MFA, missing password stage, ambiguous submit, and missing secrets more clearly; tray shows the single-site reason.
- Duplicate website now durable-saves immediately (no silent unsaved draft).
- Getting started adds **Work sites (ThousandEyes / GitHub / npm)** coaching route.
- Preserve vault crypto format and AppId. Development preview only.

# v0.7.48 — Vault recovery UX + atomic website delete

- Delete website removes workflow + credential (+ workspace ID refs) in one encrypted SQLite transaction.
- Incomplete/corrupt vault startup offers restore from `%LOCALAPPDATA%\LaunchFlow\recovery`, or quarantine + `.launchflow` portable import — never silent overwrite.
- Unlock “forgot password” copy prefers backup/restore before destructive reset.
- Troubleshooting guide adds HELP-06 for broken vault recovery.
- Added `docs/NATIVE-WINDOWS-ACCEPTANCE.md` checklist for real PC gates.
- Preserve vault crypto format and AppId. Development preview only.

# v0.7.47 — KISS help coach + durability/Teach ownership

- Getting started coaches one action at a time: first-run hides optional routes, main window shows a live guide strip, **Show target** / **Open this area** safely open the matching tab when the editor is clean, and misses auto-expand recovery help.
- New website create writes credential + configuration in one vault transaction when a password is supplied (no orphan secret on failed config save).
- Removing a website password also clears AutoLogin / KeePass / login-mode prefs in the same encrypted transaction.
- Teach/Repair admission moves to `TeachUiOperationController` (mirrors Run lease ownership); lock/drain uses the controller’s active task.
- Empty-state “3 easy steps” aligned with Add → Test → optional later.
- Preserve vault format, AppId, workflow IDs and Argon2id/AES-GCM envelope. Development preview only.

# v0.7.45 — Clearly scoped website password manager

- Add an explicit website-account selector at the top of Settings → Passwords. The manager synchronizes with the workflow editor so login mode, KeePass entry and credential operations target the same website.
- Display the selected website's hostname and a reminder that only this workflow's encrypted login is changed; no global website-password change exists.
- Reject credential saving or deletion when the visible site selection does not match the selected workflow ID.
- Warn before switching websites if replacement login fields have unsaved values, rather than quietly dropping them.
- Keep the password masked; make the **new** username/email editable and readable. Existing saved values are never prefilled or displayed.
- Add UI structure and isolated-site credential regression tests; preserve the existing encrypted vault location, workflow IDs, master password and installer AppId.
- Bump development-preview version to v0.7.45. No credential migration is required.

**Unsigned development preview only.** CI and synthetic UI tests are not real account or native installer upgrade acceptance.

# v0.7.44 — Adaptive one-page and two-stage login (development preview)

- Detect a visible username and password on the same page and fill both normally.
- Detect email-first pages, safely advance via a unique recognized Next/Log In button, then fill the password after navigation.
- Resume from an already-open password page, including when the earlier username field is missing or read-only.
- If the final submit selector is unavailable, recognize a unique local password-form Sign In/Log In button without requiring a recording. Never guess among multiple candidates.
- Keep existing HTTPS, session/vault, credential-document and origin approval guards. A new IdP origin requires explicit approval; MFA, SSO handoffs and challenges remain manual.
- Add isolated real-Chromium regression cases for both form layouts, mismatched second-stage buttons, resumed sessions and ambiguous submits; preserve the cross-origin and MFA tests.
- Increment version numbers without changing encrypted vault schema, workflow IDs, profile locations or installer AppId.

**Unsigned development preview.** Browser fixture tests do not constitute live ThousandEyes verification or native Windows installer acceptance.

# v0.7.43 — Automatic email-first / password-second login

- LaunchFlow now handles ordinary staged login pages without recording separate username/Next/password actions. After filling the username it identifies a single visible Log In / Next / Continue button, safely clicks it on the previously approved document, waits for the password stage, and fills/submits saved credentials.
- Works across same-document UI transitions and cross-page/domain redirects. Cross-origin identity-provider passwords still require explicit user origin approval; local login and SSO are never conflated.
- Ambiguous buttons and MFA / security-code screens require manual attention instead of guessed clicks, credential retries or bypass attempts.
- Keep the default-browser open-only mode, encrypted vault format, monitor positioning, existing login health checks and manual-capture workflows unchanged.
- Add real isolated Chromium regressions for automatic two-stage login, signed-in page reuse without duplicate credential fill, denied and approved cross-origin IdPs, MFA barriers and ambiguous button refusal.
- Version application, Inno Setup, fallback metadata and source report to v0.7.43.

**Unsigned preview.** ThousandEyes local versus organizational SSO routing varies by tenant; real tenant login is not exercised by synthetic CI fixtures. Do not automate MFA or bypass organizational application controls.

# v0.7.42 — Readable username/email in create-website wizard

- Replace the masked WPF UsernameBox with an ordinary visible TextBox. Users can verify their email address or login name before creating a website.
- Keep the actual PasswordBox masked and preserve the existing explicit encrypted credential-storage path; recorded actions still exclude login details.
- Read the username from TextBox.Text, including the existing insecure-HTTP confirmation path, while continuing to read the password from PasswordBox.Password.
- Keep cancel/close clearing pending form fields and preserve optional-login behavior.
- Add a WPF XAML/source regression check to prevent accidental username masking or password disclosure.
- Bump Windows application, installer, version fallback, and file inventory to v0.7.42.

**Unsigned development preview.** Preserve existing vault files. Actual native interactive wizard testing on Windows remains a user acceptance gate.

# v0.7.41 — Transactional-save architecture test build

- Preview build of the isolated transaction/validation/publication work and encrypted vault-session protection from the pinned v0.7.40 baseline.
- Includes regression tests for rejected SQLite writes, cold encrypted database reopen, stale vault authorization and auto-save reentrancy.
- No intentional encrypted vault schema migration, workflow-ID change, or profile-path change.
- Separate version number for this test installer, as required for each new user-visible build.

**Development preview, not a stable release.** Unified lifecycle coordination and browser-session ownership milestones remained unfinished at this preview. Avoid relying on this test build for production credentials until native Windows upgrade and lock/unlock acceptance testing.

# v0.7.40 — Encrypted auto-save

- Add a 900 ms debounced auto-save for website and application-setting edits. Validation and encrypted durable persistence complete before the UI reports that changes are saved.
- Auto-save manually edited or added workflow steps, including waits and navigation actions. Recorder-captured actions are committed immediately with `ConfigStore.SaveDurable`, not a potentially volatile in-memory fallback.
- Preserve the existing create-workflow immediate encrypted save and explicit pre-test save barrier.
- Display **Unsaved changes**, **Saving…**, **All changes saved**, or **Save failed**. Failed writes keep changes dirty, show **Retry save** even when the toolbar is compact, and do not produce repeated modal prompts.
- Never auto-save entered password/username secrets: these remain protected behind **Settings → Passwords → Save credentials**.
- Pause auto-save while vault is locked, a recorder/editor is active, Windows is locked, or application shutdown is in progress; retain pending changes for existing exit/update gates.
- Avoid repeatedly registering Windows autostart tasks on every unrelated edit by caching the last successfully applied registration preference.
- Add pure scheduler unit tests for debounce, lock deferral, failure retention and retry, plus WPF source contract tests.
- Carry forward the balanced centered toolbar fix from v0.7.39, and bump application, installer, UI tests and inventory to v0.7.40.

**Development build only.** CI and synthetic browser tests do not substitute for interactive Windows testing or publisher signing. Existing encrypted vault format and profile paths remain unchanged.

# v0.7.39 — Balanced main toolbar

- Rebuild the custom-window action toolbar as a symmetrical three-column WPF Grid, with equal-width left and right columns and an auto-sized true-center column.
- Keep website creation/saving on the left and optional help actions on the right, while Run all and Stop remain centered regardless of available window width.
- Give the toolbar more vertical space, matched control heights and a subtle action-group surface.
- Use a compact-width breakpoint to hide only the verbose Save status caption when it would crowd the primary controls; unsaved state remains visible via the Save button's weight and tooltip.
- Add a source-level regression assertion for true centering, control identity and responsive behavior.
- Bump application/installer/fallback metadata and source-line inventory to v0.7.39. No encrypted vault schema, profile or workflow changes.

**Status:** Windows development test build, unsigned. Native interactive visual acceptance at multiple DPI settings remains necessary.

# v0.7.38 — Testable service boundaries

- Move workflow URL and HTTPS credential-origin validation and durable-save admission into **WorkflowConfigurationService**; inject the actual encrypted persistence operation and URL policy instead of referencing WPF controls or singleton state in this service.
- Extract **StartupRegistrationService** with injected Task Scheduler and Run-key adapters, centralizing the mutually-exclusive Windows startup policy.
- Extract **SessionLockPolicy** to make the configurable lock threshold a pure, deterministic rule. Preserve the 10-minute-by-default threshold and fail closed on backwards time intervals.
- Wire all three services into the existing MainWindow and keep encrypted vault layout, application installer AppId, and workflow serialization untouched.
- Introduce a separate net8.0 service-contract test target that runs without WPF, user files, external browsers, or Windows registry modification.
- Continue the v0.7.37 partial-file decomposition while beginning the more important class-level separation of responsibility.
- Increase the application, installer, UI test and file inventory version to **v0.7.38**.

**Scope:** incremental architecture hardening, not a completed MVVM migration or a claim of independently verified production security. Hands-on Windows acceptance and signed distribution remain outstanding.

# v0.7.37 — Architecture refactor without data migration

- Split the original monolithic WPF MainWindow code-behind into cohesive partial modules for UI shell, websites, workflow persistence, execution, recorder workflow, settings navigation, credentials, password sessions, KeePassXC, import/export, workspaces, Windows lock events and startup experience.
- Separate AutomationEngine login flow, explicit sign-in confirmation and workflow step execution into documented partial modules. The core retains lifecycle, policy gating, ownership and cancellation.
- Preserve method bodies and shared state in this controlled phase; this is an incremental maintainability refactor, not yet a full MVVM or dependency-injection conversion.
- Update source-linked regression tests to read the current production partials; enforce one owner per key method and a smaller core size using `tests/architecture/check_partials.py`.
- Add a versioned, complete Git-tracked file inventory (`tools/Count-LaunchFlow-Lines.py`) to the Windows installer artifact.
- Version bump across assembly, installer and CI tests to **v0.7.37**. Retain vault schema, credentials, workflow identifiers, profile paths and installer AppId.

**Development test build only.** A passing CI installer does not constitute hands-on Windows acceptance or approved Authenticode signing. Do not migrate or delete a user's vault during installation.

# v0.7.36 — Recorder reliability and cleaner action layouts

- Recorder accepts legitimate pointer clicks that retarget from a nested icon or child element to the same button; canceled drags remain excluded.
- Show a privacy-safe connection status for the original LaunchFlow browser tab, with automatic retry of recording bridge initialization after navigation.
- Redesign the recording window around a clear connection card, captured-action count, and expandable optional tools/safety guidance.
- Separate the application's top toolbar into Add, Save/status, Run/Stop, and Help groups for clearer visual hierarchy.
- Add an actual Chromium regression for retargeted clicks and bridge readiness to the Windows build gate.
- Preserve safety boundaries: only approved keys; no typed text, passwords, emails, MFA codes, sensitive forms, private URL parameters, or arbitrary popup/iframe replay.
- Bump application, installer, metadata and build artifacts to **v0.7.36**.

**Development preview.** Automated compilation and synthetic browser tests do not replace interactive verification on target machines, nor do they sign the installer.

# v0.7.35 — Cleaner Settings and first-website onboarding

- Remove duplicate Settings category tiles; the left-hand category list remains the single way to navigate Settings.
- Move encrypted website credentials from the top navigation into **Settings → Passwords**, retaining explicit master-password authorization and clearing credential inputs when leaving the category.
- Show post-creation guidance only for the first saved website, rather than after every website addition; store that decision durably and independently of the shortcut hint.
- Update the new-website wizard, tutorial and KeePassXC messages to use the new Settings path.
- Carry forward the v0.7.34 UI startup-crash guard and safe startup diagnostics.
- Increment assembly, installer, fallback display, CI artifact, and UI source test versions consistently to v0.7.35.

**Versioning policy:** Each user-facing build with new fixes or features gets a new version, never a reused installer filename. Development commits within one build may share a version until that version's deliverable is produced.

**Development preview only.** Windows CI compilation and package checks are not the same as native interactive acceptance or Authenticode signing. No stable release is authorized.

# v0.7.34 — Websites-first clarity and sign-in mode

- More consistent website-focused language across the editor, Passwords and Settings.
- One explicit sign-in mode (saved password, no login, or SSO) determines automatic credential entry; hidden legacy flag follows that mode.
- After adding a website, show its next steps: Test this site, optional Passwords, optional Record clicks.
- Settings General offers direct category links, and the website editor links to advanced sign-in.
- Teach explains popup-tab and MFA limitations before recording starts.
- Legacy workspace-group data is preserved with a compatibility explanation.
- Source-linked tests cover sign-in mode and setup guidance.

**Preview only.** Native Windows acceptance and trusted publisher signing remain required. Not a stable release.

# v0.7.33 — Clear first-time website setup

- Start page displays **3 easy steps** and an interactive getting-started guide.
- Add website wizard uses numbered sections for URL, browser/screen and optional automatic login. Connection protocol/port and credential fields are hidden until needed.
- Website editor highlights **Record clicks** and **Fix sign-in**, with advanced steps inside a collapsed More actions section; existing commands still work.
- Clearer explanations for Test this site, Run all and Save changes. Tutorial matches the new flow.
- No change to vault format, passwords, existing workflows, record/replay, startup or original navy/mint theme.
- Windows UI and installer/browser regression checks required. Preview only; do not publish or merge without acceptance.

# v0.7.32 — Verified signed Windows distribution guard (developer review)

- Publisher-signed distribution builds now require a configured signing identity and SignTool **before** touching build output. Unsigned installer smoke tests can no longer be described as ready for App Control enforcement.
- Sign and verify unsigned published EXE/DLL dependencies **recursively**, including native SQLite and nested browser-runtime binaries, while preserving existing valid signatures.
- Verify Setup's Authenticode signature before marking a signed distribution build complete; add a negative installer builder test that verifies refusal preserves existing build artifacts.
- Keep v0.7.31's SQLite preflight before vault/master-password initialization, with safe error messages for Windows policy blocks.
- Explicitly require target-machine acceptance under its actual App Control/Smart App Control policy, separate from CI success. A signed binary may still be denied by an explicit policy. Windows 10/11 x64 supported platform; other operating systems and CPU targets not claimed.
- No vault schema, stored credential, login flow, installer AppId or current stable release changes.

**Preview only:** A real approved publisher certificate is not available for GitHub's unsigned smoke builds. Do not hand an unsigned CI ZIP to a user as a fix for 0x800711C7.

# v0.7.31 — Windows Application Control preflight and signed-dependency support

- Detect App Control / WDAC block `0x800711C7` on `Microsoft.Data.Sqlite.dll` and other required SQLite dependencies before the first-run master-password wizard or any vault read/write.
- Run only an in-memory SQLite runtime check. A rejected or missing dependency produces actionable guidance for the machine owner/administrator without printing credentials or modifying the vault.
- When an authentic publisher certificate is configured, the Windows installer builder signs unsigned top-level managed/native DLLs and EXEs, preserves valid existing vendor signatures, checks resulting signatures and fails closed if signing is incomplete. Unsigned local test builds remain explicitly unsupported on restrictive managed computers.
- Add source-linked tests for policy failures, wrapped exceptions, missing dependencies and credential-safe errors; validate old encrypted data and login workflows unchanged.
- No vault schema, encryption, installed browser policy, app identity or stable update channel changes.

**Test status:** Developer-only preview. A signature still does not authorize a build under an organization’s App Control rules. Native Windows/WDAC acceptance and trusted publisher approval are required before use on a policy-enforced computer.

# v0.7.30 — Nonblocking windows by default

- Changed **Sign-in needs attention** from a modal dialog to a modeless window, keeping the main LaunchFlow interface and browser clickable while the user reviews the problem.
- The run completes and re-enables its controls immediately without waiting for the sign-in window to close; one window per website prevents duplicates.
- **I'm signed in** and **Repair login** now apply asynchronously to the original website after the window closes and only if the vault/session and imported workflow revision remain valid. Closing the window takes no action.
- About, Browser Setup and Workspace Preview are now modeless read-only windows.
- Password, credential, destructive-change and critical confirmation prompts retain blocking authorization semantics.
- Windows UI regression fixtures validate modeless behavior, no duplicate messages and the original website identity across a selection change.
- The original dark navy/blue color scheme and vault/profile database formats remain unchanged.

**Unsigned developer preview.** Check Windows focus and multi-monitor placement manually before release.

# v0.7.29 — Explicit session sign-in confirmation and cross-site Teach recording

- A clear **I'm signed in** option appears alongside **Repair login** and **Not now**. Explicit confirmation applies only to the exact current HTTPS browser page and origin, not future launches or other sites.
- A visible password/MFA challenge and changed browser session cannot be manually marked as authenticated. After confirming, click Run this site again to continue recorded steps without attempting password entry a second time.
- Teach retains browser click capture across normal same-tab navigation and automatically adds a safe **Navigate** action for a change of website origin before the next click. URLs containing query parameters, fragments, user info or policy-blocked sites are never automatically stored.
- New popup tabs remain unsupported for automatic click replay; explicitly add the page only when safe. Existing secret, keyboard and iframe recording safeguards remain active.
- Chromium tests cover manual confirmation, challenge rejection, same-tab cross-site capture and OAuth query protection. Installer/data formats unchanged.

**Developer preview:** Manual acceptance needed for live NPM and Windows multi-window browser behavior. Never merge before independent testing.

# v0.7.28 — installer safe-exit and authenticated-dashboard verification

- Installer shutdown now distinguishes actual unsaved work from browser/profile cleanup failures and retries a transient browser-close failure without killing browser processes or discarding encrypted profile data.
- Password Manager internal tab changes during installer shutdown do not create a spurious dirty flag after settings have been durably saved. Genuine edits or failed cleanup still block the installer safely.
- Auto-login recognizes a completed **same-origin** HTTPS navigation from a login page to a non-login dashboard even if an old completion selector is stale, including when the Submit button detaches during navigation.
- MFA challenges, error routes, wrong-origin navigation and visible password prompts are not considered successful authentication; the existing policy/origin checks still apply.
- Synthetic Chromium regression fixtures cover a dashboard redirect and rejected MFA redirect. No vault/profile schema changes or forced installer task termination.

**Preview only:** Test v0.7.28 on Windows with a backup of encrypted data. Older running LaunchFlow versions may still need a manual Exit from the system tray before Setup can proceed.

# v0.7.27 — Installed-browser detection and restored navy theme (development preview)

- Scan Windows App Paths (both registry views) and conventional installation folders to offer only installed Edge, Chrome and Firefox browsers in both the new-website wizard and existing website editor. Re-scan whenever the selector opens.
- Always keep the explicit Default browser (open only) choice; refuse automatic sign-in under open-only mode.
- Preserve saved browser names when a browser has been uninstalled, label them as unavailable and fail closed on launch instead of silently switching to Edge.
- Restore the prior dark navy/blue presentation and mint accent colors while retaining the new compact UI, custom dropdowns and editor layouts.
- Add production-linked browser detection tests and source/GUI regression contracts.
- Keep encrypted vault data, sign-in policy, installer AppId and profile formats unchanged.

**Status:** Unsigned Windows test preview. Real Windows browser detection, installer upgrade and multi-monitor layout still require hands-on acceptance.

# v0.7.26 — Universal automatic sign-in repairs (development preview)

- Wait for JavaScript-rendered credential inputs without allowing stored passwords to retarget across a document change.
- Prefer visible enabled inputs and Submit controls over hidden placeholder elements.
- Require all configured credential fields to be filled before submitting; missing credentials no longer report a successful sign-in.
- Recognize explicitly configured, already-authenticated browser sessions without treating generic BODY/HTML elements as success.
- Preserve approved HTTPS origins, credential-policy checks, manual MFA/SSO handling and secret-safe diagnostics.
- New synthetic browser cases: delayed form rendering, hidden field/button duplicates, missing required password, two-stage username/Next/password login, and existing signed-in sessions.
- Consistent v0.7.26 app, unsigned installer and CI artifacts; vault data and profile formats unchanged.

**Release status:** Developer test only. Validate real-browser login flows and Windows upgrade/lock/unlock before releasing.

# v0.7.25 — Explicit-exit and editor-lifecycle hardening (development preview)

- Prevent tray Exit and normal window Exit from losing pending edits while the encrypted vault is locked or a durable write fails. Clean locked vaults still exit without prompting.
- Keep LaunchFlow open if a New Website wizard, Step Editor or Teach recording remains active during a requested exit or automatic installer shutdown.
- Require durable encrypted configuration persistence before displaying credential-save success, and track duplicated website workflows as unsaved until saved.
- Add production-linked exit-safety scenarios and a UI credential-save durable-write regression, plus Windows source contracts.
- Updated the app, installer and unsigned CI test artifact version to 0.7.25. The AppId, database/vault schema, profile formats and updater policy remain unchanged.

**Release status:** Developer preview only. Real Windows WPF dialogs, 10-minute session lock, DPI/physical monitors, DPAPI, encrypted SQLite import/restart, and installer upgrade/rollback remain native verification gates. CI and cross-builds are not release authorization.

# v0.7.24 — Independent review P0 repairs (development preview)

- **Data-loss prevention:** Automatic installer shutdown refuses exit if unsaved edits exist and the vault is locked. A clean locked vault can still exit; unlocked configuration must be durably saved before automatic exit.
- **Modal reentrancy:** Add/Edit website step actions capture workflow object identity, step identity/index and configuration revision, and revalidate after the dialog returns. An encrypted import or selection change cannot apply an old edit to replacement data.
- **Keyboard recorder replay:** Tab, Escape and arrow keys on ordinary buttons/links are captured rather than dropped; Enter on buttons still produces one Click (not a second Press).
- **Modifier handling:** Shift+Tab and unsupported combinations are explicitly skipped with a visible recorder warning instead of being misreported as plain Tab.
- **Recorder privacy:** Shadow-root contexts, credential-related fields, sensitive autocomplete attributes, form-associated password fields and non-native controls are rejected. No typed text or passwords are recorded.
- **Save state:** Manually editing the KeePass entry path marks configuration dirty.
- Production-linked modal guard and browser recorder regressions added; independent encrypted-import and native Windows review remains required.

**KISS release approach:** default-safe; no extra configuration screens, no hidden save failure, no keylogger. Existing encrypted storage format, installer identity and policy settings remain unchanged.
**Not release approved.** Real Windows DPI, vault lifecycle, source-specific repro and installer behavior need independent review.

# v0.7.23 — Usability and privacy-preserving Teach recorder (development)

- **Website actions:** Ctrl/Shift multi-select, Select all, Delete selected (bulk deletion requires confirmation), edit one action by double-click, and a one-click Wait +1s action; wait steps can also target a selector for slow pages.
- **Teach recorder:** multi-select, edit, remove, Select all and explicit wait controls. Only pre-approved non-text navigation keys (Enter, Tab, Escape and arrows) may be recorded; text input, password/email field and password-containing form keystrokes are blocked. Typed searches and login credentials are NEVER recorded; use explicit Fill only for non-secret text and Passwords for encrypted logins.
- **Help:** the guide opens modelessly, allowing people to click the controls while reading instructions. Added annotated, privacy-safe example interface diagrams; these are illustrations, not screenshots of users or websites.
- **Save feedback:** status label changes to Unsaved changes after editing website details or action steps and to All changes saved after successful persistence.
- **Recorder usability:** restores the main LaunchFlow window to the foreground when Teach closes.
- **Maximize:** custom-window chrome restricts maximized bounds to the current monitor's work area to avoid covering taskbar and neighboring displays.
- **Slow pages:** click actions retain Playwright's normal locator auto-wait; explicit Wait-for-element actions can wait up to 60 seconds, and the editor explains both.

**Security constraints:** no typed text, emails, passwords, OTP or tokens collected by the recorder, no auto-screenshot capture of sensitive pages, no change to existing encrypted vault schema or profiles. Real Windows usability, DPI and multi-monitor acceptance still required before public release.

# v0.7.22 — Websites-first sidebar (development preview)

- Removed **Workspace Profiles** (saved-workspace selector, name field, Save/Preview/Run workspace buttons) from the visible interface.
- Renamed the main Workflows navigation and creation action to **Websites / New website**; the sidebar shows just the websites list, current status and website actions. Run all still launches enabled websites.
- Preserved previously saved workspace records in the encrypted configuration unchanged. A collapsed, non-interactive compatibility panel keeps the former WPF event and import contracts intact for now, preventing an unsafe storage migration just before release.
- Added UI contract assertions that the old panel is collapsed, not hit-testable or focusable, and its old visible labels are absent.
- Updated application/installer/CI versions to **0.7.22**. This remains an unsigned developer preview, not a stable release.

**Known limitation:** Workspace profiles are preserved but cannot be edited from the simplified UI. The user can continue working with individual websites. Restore legacy workspace management only in an explicitly designed future feature, if needed.

# v0.7.21 — No-login workflows, safer installer updates and workspace UI (development preview)

- New workflows default to **No login required**, avoiding incorrect MFA/sign-in prompts on local dashboards; saved-credential login remains an explicit choice.
- Added regression coverage for no-login pages whose content contains verification keywords.
- Made workspace Save/Preview labels fit the narrow sidebar.
- Added a graceful installer shutdown request and fail-closed cleanup before replacing application files. Previously installed builds may still need one manual close on their first update.
- Retained workspace import/revision validation, durable saves, encrypted-vault safety and regression gates from the preceding development branch.

**Development build policy:** Every new test installer delivered to the user gets a higher patch version (next build after v0.7.21 is v0.7.22). Multiple internal commits may share a version until that build is delivered; all version surfaces and artifacts must agree. A development preview is not a published GitHub release and will not appear in the in-app stable updater.

**Release status:** Experimental, not release-approved. Native Windows shutdown, UI scaling and real-profile restore/upgrade acceptance are still required.

# v0.7.20 — Workspace UI refresh and selection fixes (testing build)

- Redesigned the main window with clearer navigation, compact workflow actions, improved visual hierarchy and a consistent dark teal theme.
- Rebuilt the Workspace Profiles panel with a selectable saved-workspace dropdown and a separate field for naming workspaces.
- Fixed workspace selection, preview, save and launch behavior; running a saved workspace no longer depends on editable ComboBox text.
- Improved dropdown labels and popup presentation, workflow editor spacing, credential settings, and dialog consistency.
- Added in-app version indicators and improved the update-settings presentation.
- Preserved existing encrypted vault data, workflow IDs and the stable Inno Setup application identity.
- Added WPF XML, UI-control wiring and release-version checks to CI.

**Release status:** Experimental branch. The app only discovers updates from published GitHub Releases; changing the branch version alone does not publish an update. Verify native Windows UI behavior, installation and profile compatibility before releasing.

# v0.7.19 — Teal switches and multi-click recording

- Removed checkmark icons from checkbox controls, replacing them with compact on/off switches in LaunchFlow teal.
- Matched the workflow Enabled switch to the same blue-green accent.
- Record every user click, including form-field clicks, without collecting entered values or passwords.
- Register Teach listeners across browser navigations, child frames and newly opened tabs.
- Capture pointer clicks before redirects and keyboard-activated clicks as fallback.
- Continue showing the number of captured actions and give in-flight callbacks time to finish when saving.

**Testing:** Requires a native Windows compilation and practical Teach-session tests before release.

# v0.7.18 — Simpler interface and green enabled controls

- Checked boxes and the workflow on/off switch now use a consistent solid green.
- Removed the Activity page and hidden developer-only diagnostics from the user interface.
- Removed manual database backup/restore controls, first-run copies, periodic database backups and backup reminders.
- Kept password-protected Import and Export for moving websites between computers.
- Simplified security, update and export descriptions for everyday users.
- Kept existing encrypted vault data and files untouched during upgrades.

**Status:** Source validation only. Windows compilation and UI testing still required.

# v0.7.17 — KeePassXC setup and workflow toggle

- Replaced the Enabled badge with a compact switch.
- KeePassXC databases can be detected from familiar Windows folders, chosen from a list, or browsed.
- Added a searchable KeePassXC login picker in Passwords, without manually entering paths.
- Moved key file and manual entry path settings to Advanced.
- Only login names are displayed; no KeePassXC passwords are imported.
- Removed the manual KeePassXC path field from the New Workflow wizard.

**Status:** Source verification only. Test native Windows compilation and KeePassXC workflows before release.

# v0.7.16 — Independent website setup and local network URLs

- New Workflow remains visible when the main window is minimized.
- Add websites using HTTP, HTTPS, local IP addresses, localhost and custom ports.
- Preserve the protocol in pasted full URLs and warn about HTTP when saving credentials.
- Keep New Workflow modeless with one setup window at a time.
- The Windows builder requests shutdown of LaunchFlow before rebuilding and uses a forced close only as a fallback.
- Fixed the Playwright import for the Teach recorder.
- Preserved encrypted vault data and the existing Inno Setup AppId.

**Status:** Static verification required; Windows installation and runtime tests are still needed.

# v0.7.12 — Reliability and packaging cleanup (development)

- Fixed modeless Teach saving steps to the wrong website if the selection changes.
- Prevented overlapping Teach sessions and handled removal of a website during recording.
- Fixed the Playwright page-initialization script so Teach listeners run across navigations.
- Kept sign-in progress intact when opening Teach or Repair login on an existing browser page.
- Improved installer builder reliability: require an exact versioned Setup file and bundled .NET runtime dependencies.
- Clarified that end users need neither the .NET SDK nor Inno Setup.
- Preserved the existing installer AppId and encrypted vault location.

**Status:** Source checks only. A native Windows compile and real install/upgrade test are still required.

# v0.7.11 — KISS-rule usability and Teach clicks (experimental)

- Made Teach a movable non-modal window so the main app and browser remain usable.
- Start recording clicks automatically; live click updates survive normal page navigations.
- Save captured clicks when the user chooses Save clicks; never record typed input or passwords.
- Added a four-step getting-started guide, available anytime from Help.
- Simplified labels in Teach and the website setup wizard.
- Preserved the encrypted vault and existing installer ID.

**Testing:** Windows compilation and login-flow tests are required before merging.

# v0.7.10 — First-launch workflow display (development)

- Fixed a startup UI issue where the Workflows page stayed blank until switching to Passwords or Settings and back.
- Realize the headerless WPF TabControl after Loaded and reselect Workflows without triggering normal tab-change save/password handling.
- Populate the editor from the initially selected saved workflow; show the empty-state page only when there are no workflows.
- Updated application and installer versions to 0.7.10; the upgrade AppId and encrypted vault data stay unchanged.

**Build status:** Still requires a Windows compile and fresh-launch test before release.

# v0.7.9 — Integrated title bar and navigation (development)

- Consolidated Workflows, Passwords, Settings, and actions into the custom Windows title bar.
- Added minimize, maximize/restore, and close controls while retaining native window behavior.
- Introduced a Settings sidebar with a separate Updates section.
- Improved alignment of the workflow Enabled toggle.
- Updated app, Windows Setup, About dialog, and build/validation scripts to 0.7.9.
- Preserved the installer AppId and encrypted vault location for upgrades.

**Status:** Windows compilation and installation testing still required before a public release.

# v0.7.8 — Explicit credential saving and masked usernames

- Added **Save credentials** to the Passwords tab. It updates the encrypted vault only when clicked.
- Usernames/email addresses are now masked during entry, alongside passwords, with **no Show/Hide option**.
- Previously saved usernames and passwords are never populated into the visible editor.
- On existing credentials, an empty username or password field retains the old value. Nonempty fields replace saved values.
- Leaving the tab or using the main Save button no longer silently overwrites login details.
- Updated the New Workflow wizard to mask username/email entry too.
- Kept database schema, encryption, installer AppId, upgrade location, and workflow IDs unchanged.

# v0.7.8 — Windows build fixes

- Fixed the ambiguous WPF `Color` reference (`CS0104`) in `DarkWindowChrome.cs`.
- Explicitly discarded the two intentional asynchronous dispatcher operations (`CS4014`) in `App.xaml.cs`.
- Corrected the nullable-dictionary type passed to Playwright CDP (`CS8620`) in `AutomationEngine.cs`.
- Fixed the Windows CI installer workflow's stale v0.7.3 output file references.
- Kept the same Inno Setup AppId, database location, and vault format to support in-place updates.
- Added `tools/Verify-LaunchFlow-Source.py` for source/configuration regression checks.

**Verification:** Static checks run in the preparation environment. The Windows WPF compiler, installer, and running application must still be tested on Windows.

# v0.7.5 — Restore, login pause, uninstall wipe choice

- **Restore from backup** — Settings → Security replaces the live vault with a `launchflow-*.db` copy (safety copy kept first)
- **Open `.launchflow` from Explorer** — registered file type and second-instance pipe deliver the path into Import
- **Broken login pauses steps** — MFA/SSO/password-still-visible stops further steps for that site, tray warning, offer Repair login
- **Uninstall keep vs wipe** — Inno asks whether to delete `%LOCALAPPDATA%\LaunchFlow` (No = keep vault, recommended)

# v0.7.4 — Dark checkboxes, tighter editor, Authenticode path

- Custom dark Fluent-style checkboxes (teal check, focus ring)
- Tighter workflow editor spacing and shorter action labels
- Hardened Authenticode: `tools/Sign-LaunchFlow.ps1`, Inno `SignTool=lfsign`, `tools/New-CodeSigningCert.ps1`, `CODE-SIGNING.md`
- Builder reports signing status; signs app + setup/uninstaller when a cert is configured

# v0.7.3 — Icon + dark scrollbars

- Original LaunchFlow app icon (teal flow → monitor) for window, Start Menu, and tray
- Dark Fluent-style scrollbars app-wide (no light Windows chrome)
- Dark Windows title bars via DWM immersive mode
- Darker PasswordBox / ListBox item hover-selected states

# v0.7.2 — Release channel, repair login, click capture

- GitHub Actions **Release Windows installer** publishes `LaunchFlow-Setup-*.exe` to a GitHub Release (tag `v*`)
- Optional signing secrets: `LAUNCHFLOW_SIGN_PFX_BASE64` + `LAUNCHFLOW_SIGN_PFX_PASSWORD`
- **Repair login** + post-login health heuristics (MFA/SSO/password still visible)
- Visual recorder **live click capture** (skips password fields)
- Windows Hello periodic password re-auth (30 days)
- Keyboard focus ring on buttons; more AutomationProperties
- Builder opens setup only with `-LaunchSetup`; finds any `LaunchFlow-Setup-*.exe`

# v0.7.1 — Hardening pass

- Vault reset, Hello unlock, sealed profiles, Task Scheduler, backups, update check hooks

# v0.7.0 — Consumer-ready shell

- About, welcome, tray toasts, consumer installer copy
