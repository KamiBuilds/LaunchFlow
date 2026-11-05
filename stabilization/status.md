# LaunchFlow stabilization status — M0 accountability

**Updated 2026-11-04.** This is a working engineering status document, not proof of native application acceptance.

## Exact source and artifact control

| Item | Verified value |
|---|---|
| Protected baseline | `main` at `d5f7e692ac064ab971d219b0d4c221d6e2cb47c6` — untouched |
| User-approved surviving working branch | `validated/v0.7.46-three-checks-20261103` |
| Source frozen for this M0 review | `941c5f13bd993ffa18e0d883e77448379f7b54a5` |
| Frozen source tree | `3af53c0b048f0eac5f471df8cfa045bd687f3cf4` |
| Last independent review candidate in contract | `34befa061ca30097808a2acc0b9270220b0f5354` / tree `7b93d5d016f904191b85727939a3294421781d40` |
| Verification | [Actions run 41687485233](https://github.com/KamiBuilds/LaunchFlow/actions/runs/41687485233) — Linux, Windows, artifact readiness all PASS |
| Installed test artifact | GitHub Actions artifact `LaunchFlow-v0.7.46-941c5f13bd993ffa18e0d883e77448379f7b54a5-Windows`, ID `13256691698`; no native test record |
| Version/product identity | v0.7.46 unchanged |
| Remote branch inventory before M0 | Only `main` and the validated branch (user-directed cleanup executed). The 34 archived branch tips are in the successful [pre-cleanup recovery artifact](https://github.com/KamiBuilds/LaunchFlow/actions/runs/41685214053). |

**Historical discrepancy:** the contract's G15 preservation list includes `develop`, `hermes`, `both`, `both-2`, `both-3`, which were already removed by the separately authorized branch cleanup. Do not pretend these refs exist or restore them without a decision. GitHub's historic open stacked PR #1–#26 metadata is not equivalent to live branch refs. `AGENTS.md` still references `hermes`, which is stale for this repository state.

## Completed vs open milestones

| Milestone | State | Exact evidence | Remaining gate |
|---|---|---|---|
| M0 baseline/control | **In progress** | 231-file recursive manifest; overview and 36-family acceptance matrix drafted; source and artifact identity recorded | Confirm all file ownership/consumers/tests; freeze reviewed M0 doc commit, independent source review |
| M1 six critical login regressions | **Automated repair verified, native pending** | RED independent run `41645119359`, final GREEN run `41687485233`; retained old L1/L2, L3, 32 document-bound cases | Native/vendor sign-in acceptance and independent final-tree review |
| M2 durability/lifecycle | **In progress; A2 code/automated tests GREEN** | A2 RED [41793237279](https://github.com/KamiBuilds/LaunchFlow/actions/runs/41793237279), GREEN Linux [41794525946](https://github.com/KamiBuilds/LaunchFlow/actions/runs/41794525946): 43/43 UI tests, all 20 Linux stages | A1 and A3 remain open; native Windows Repair/Exit acceptance unexecuted |
| M3 controlled extraction | **Not started** | Target ownership/direction written | One bounded slice with consumer/link preservation and regression suite |
| M4 product/native | **Blocked by external acceptance** | CI uses simulated/production-linked browser/seams | Real install/upgrade, WPF lifecycle, accessibility, vendor login, multiday use |
| M5 final integration/delivery | **Not started** | Unpublished artifact is available for test | Exact-tree independent approval, native evidence, explicit release authorization |

## Priority defects and bounded next milestone

| ID | Severity / basis | Exact source condition | Required independent test | Completion state |
|---|---|---|---|---|
| A1 / AC27 | Critical lifecycle, source-only | `ExitFromTray` calls `Close`; `OnClosed` is `async void` and drains run/Teach/engine after WPF closure; installer does pre-close await | ordinary/tray Exit with delayed browser cleanup, dirty editor, lock race, failed close; require no premature process/window termination | **Open, not reproduced by an executable RED probe** |
| A2 / AC23 | High concurrency, RED reproduced, automated GREEN | Older `RepairLoginCoreAsync` admitted modeless Repair during newer run B | Source-linked `UiHarness` test: dialog A retained, run B active, click Repair A; assert no save/recorder; prior idle Repair control retained | **Repaired in `MainWindow.Execution.cs`: run/engine admission, reserve Teach before SaveAll, revalidate original config/revision/vault; native WPF pending** |
| A3 / AC07 | High durability, source-only | credential `_vault.Save` precedes independent `_store.SaveDurable` for AutoLogin/KeePass preference; generic catch does not distinguish committed secret | induced preference-commit failure after encrypted secret commit, actual cold reopen, exact partial/atomic result with no unsafe rollback | **Open, no RED test yet** |
| F1–F6 | Previously independently reproduced | verification proof, password mapping, missing final password, cancellable approval, custom submit, SSO postcondition | retain original probes and mapped synthetic fixture changes; verify both controls and negative outcomes in full gate | **All six synthetic cases GREEN** |
| Native Windows | External | WPF Exit/Repair/session interaction and installer/upgrade not exercised in controlled CI | real clean installation, upgrade + vault preservation, lock/unlock/tray/recording | **Unexecuted** |
| ThousandEyes/IdP | External | synthetic two-stage flow is not real region/tenant/password stage | authorized user-run method and post-login UI evidence; redact account identifiers | **Unexecuted** |

## No scope shortcuts

- No vault/profile migration, data path change, Workflow ID rewrite, installer AppId change, version bump, merge into main, force push, publication, signing purchase or live credential experimentation.
- Site entry, verification challenge, HTTPS, browser origin consent and mapped password destination are distinct predicates.
- Keep README and UI/installer claims accurate; a green CI step proves only its executed assertions.
- Do not move broad file sets during a behavior fix.
- Do not claim a source scan replaces actual per-file review.

## Bounded M2/A2 completion and next engineering boundary

- A2 test-only RED commit `96bb70ed1bab801cc31a6afe9cf689bbd35cbe53`: [run 41793237279](https://github.com/KamiBuilds/LaunchFlow/actions/runs/41793237279) fails **only the new** source-linked modeless Repair overlap (42/43 UI seam tests). Existing idle Repair remains a positive control.
- Production repair commit `46c01fd27cb499c29631aef781ae1c8097df1998` reserves the Teach operation before `SaveAll`, checks active run/engine ownership and preserves current workflow/reference/revision/vault checks. [GREEN Linux run 41794525946](https://github.com/KamiBuilds/LaunchFlow/actions/runs/41794525946): 43/43 UI seam cases, independent login probes zero failures, 32/32 document-bound cases and all 20 Linux steps passed. Full Windows + artifact gates for the current documentation head must still be checked separately.
- **Next high-risk slice A1 / AC27:** characterize ordinary/tray Exit with a stalled close, active Teach/run and failed profile seal in a native-aware test. Require that WPF Closing is deferred until owned cleanup completes; do not claim managed shutdown protects every hard OS kill.
- **Next A3 / AC07:** cold encrypted SQLite regression for credential commit followed by preference-save failure, then choose a narrow atomic transaction or honest partial-success semantics.
- Full 231-file method/consumer ownership audit, native Windows, and live vendor login acceptance remain open.

This status is intentionally conservative. It distinguishes what can be built and proven in GitHub CI from what can only be accepted on an authorized Windows machine or live service.

## M2/E0 narrow packaging safety slice — 2026-11-04

- **Source-grounded RED at baseline `97753bf0029a6869b1314aa9294f36939542cf5c`:** all three static silent-contract characterizations failed: unguarded setup `MsgBox`, uninstall delete prompt without `UninstallSilent` short-circuit, no running-app uninstall guard. These are source findings, **not** Windows execution results.
- Changed only `installer/LaunchFlow.iss` to suppress custom errors when Setup/Uninstall is silent, make unattended uninstall explicitly preserve the vault/backups, and refuse uninstall when the same-session app is still running. Retained AppId, per-user path, HKCU registry, and interactive informed data-removal choice.
- Added source-contract checks in `tests/installer/DeploymentContract.Tests.ps1` to the existing Windows installer gate and a platform-neutral packaging handoff at `docs/DEPLOYMENT-CONTRACT.md`.
- **Not yet accepted:** Windows native silent/upgrade/uninstall behavior, cross-session coordination, signature/policy trust. **Next critical code slice remains A1/AC27**, followed by A3/AC07. No claim of completed modular rewrite.

## A1/AC27 implementation — source-linked, native acceptance open
- Source-grounded pre-change issue: tray and ordinary exit call `Close()` ahead of `OnClosed async void` engine disposal; cleanup could extend beyond window destruction.
- A single `ExitCompletionGate` and `MainWindow.Lifecycle.cs` defer normal closure until vault lock, browser closure, run/Teach drain, profile handling and engine disposal complete. The installer request delegates to the same operation; the existing dirty-editor save checks remain in the invoking surfaces.
- Test additions: actual gate implementation tests and WPF source delegation checks; native Windows event timing, OS shutdown and installed user/session behavior remain unexecuted until a controlled Windows run.
- Installer-request error notifications are nonmodal at this entry point, but nested save errors still require native unattended acceptance. A3/AC07 transactional credential+preference save remains open; login and Teach behavior are intentionally unchanged.

- A1 check failure at `4c7bce0a3ad306fcea9f58a0372a0a96ddc60438`: run [41807801443](https://github.com/KamiBuilds/LaunchFlow/actions/runs/41807801443) found the old-file-only UI test stale after cleanup guards moved into the lifecycle module. The assertion now reads current production and distinguishes profile-sealing from browser-close failures. Awaiting exact-source combined CI; this is not a native reproduction.

## 2026-11-05 isolated vault and backup integrity repair candidate

- **Frozen source:** `dc5cdfe600ecd33d2fbb6f3f74e55e8b180b5f3d`, initially verified by [Actions 41808199029](https://github.com/KamiBuilds/LaunchFlow/actions/runs/41808199029). All previous status blocks above are historical snapshots; their old "A3 open"/"A1 next" descriptions do not describe the candidate.
- **Candidate branch:** `repair/vault-backup-integrity-20261105`, never `main` or the validated branch. Product version, AppId, SQLite BLOB encryption format, Argon2id parameters, AES-GCM envelope, and profile readers are unchanged.
- **S1:** `VaultDatabase.SaveCredential` and `DeleteCredential` detach secrets from the live cache and commit the full credential rewrite in one SQLite transaction before publishing cache state. The first/later INSERT and DELETE failure scenarios use actual SQLite abort triggers, including independent cold-open controls.
- **S2:** `ChangeMasterPassword` uses a local candidate encryption key for the metadata/configuration/credential transaction. It publishes that key only after commit and revokes the session on an uncertain commit exception.
- **A3:** `SaveCredentialAndConfiguration` is the shared database commit for credential and related workflow preferences. The WPF handler clones configuration, captures workflow identity/revision/vault generation and publishes the still-current live fields only after a successful atomic commit. A rejected second write must retain old secrets and preferences.
- **B1:** Export captures the durable configuration and credentials under one vault generation gate; a locked or stale inclusive export is rejected before any file is created. A genuine unlocked empty vault and explicit config-only export remain valid.
- **B2:** The encrypted portable backup is staged in the destination directory, flushed, independently decrypted/validated, and published with replacement/rename. A staging failure retains the existing destination. Actual disk/power-loss and file-lock behavior still require native acceptance.
- **Synthetic tests:** `tests/storage-integrity` for save/delete, rotation, compound commits and salted Argon2id; `tests/backup-integrity` for export authorization and backup replacement. Branch CI includes both; source-only checks and simulated Windows behavior do not prove native installation.
- **S3 remains open:** Credential ciphertext is not bound to workflow identity. The strict authenticated-envelope migration cannot be implemented while preserving old-writer compatibility without an explicit migration/rollback decision. Never report that row reassignment is fixed.
- **Adjacent source concerns:** New-vault initialization and legacy-migration failure publication require separately bounded tests. No real-vault migration, branch merge, code signing or stable release is performed.

## 2026-11-05 follow-up — import/session and vault publication

- **Base for this follow-up:** independently reviewed branch commit `d3844d4217d370e54d1f5ef5fd1c150020f7d35f` ([green pipeline 41985489442](https://github.com/KamiBuilds/LaunchFlow/actions/runs/41985489442)).
- **Import modal RED:** commit `9b2caff654060355a10c5ce7b7eeddbf731422fd`, [run 41991494172](https://github.com/KamiBuilds/LaunchFlow/actions/runs/41991494172): both lock→unlock callbacks during import password and overwrite confirmation incorrectly admitted stale approval (2/4 cases passed). A separate initial test-only build error at `becffde16cf09e99471342b1678e72504d03b83e` was corrected without loosening expected results.
- **Import repair:** `MainWindow.Transfer.cs` captures configuration reference/revision and vault generation immediately after authorization; rejects changes following the password prompt and confirmation. The import uses `ReplaceSnapshot(..., expectedSession)`; `VaultDatabase` checks the key and generation inside the same transaction ownership gate, stages replacement model/cache, and only publishes after commit. `tests/import-integrity` extracts the actual handler and exercises it against disposable real encrypted SQLite; `tests/storage-integrity` covers denied stale writes and valid replacement cold reopening. Native WPF modal timing is still separate.
- **Initialization/legacy RED:** [run 41992427518](https://github.com/KamiBuilds/LaunchFlow/actions/runs/41992427518): the first initialization error left a live key; an invalid legacy credential blob made `Unlock` return false with an active authorization. The 17 preexisting storage cases remained green. The injected initialization fault is a disposed JSON element (a controlled serialization failure), not a disk crash.
- **Publication repair:** `Initialize` prepares configuration/secrets and serializes before writing, commits metadata/configuration/credentials in one transaction, and publishes its key/cache afterward. `Unlock` validates verifier and encrypted configuration and performs legacy migration with a local derived key before publishing session generation or active key; failure zeros only the unadopted key. Legacy records are not converted to an empty result on corrupt decode.
- **Verification limits:** Native Windows installed UI/installer, OS-bound profile encryption, backup replacement under Windows sharing failure, and complete new-vault physical failure are not certified. A partially created SQLite artifact is not silently deleted; startup classification/recovery of such an artifact may need an explicit operator-facing recovery pathway.
- **S3 stays open:** Strict credential/workflow AAD binding requires an approved versioned envelope, old-writer policy and rollback design. No format change or real-vault migration has occurred.
- **Publication hygiene:** Active CI filters now list existing branches only; retired UI smoke is manually dispatchable or runs on PRs to `main`. Current source guidance uses role-neutral wording. Historical retained audit documents may still identify old branch names or contributors; no history was rewritten or evidence silently erased.

## 2026-11-05 S3 strict v3 development branch — release gating

- Original S3 independent negative fixture copied an intact v2 credential BLOB from workflow A to row B, yielding A credentials when reading B. This is a local write-access counterexample, not remote MFA or authentication bypass.
- Strict development format uses separate `secure_meta`, `secure_objects`, and `secure_credentials`, each record authenticated with purpose, version, canonical expected owner and random per-vault identifier. `VaultRecordCodec` is the sole v3 envelope implementation; no legacy decoder is tried after v3 failure.
- Ordinary `Unlock` no longer mutates or authorizes v1/v2. The user must explicitly approve `UpgradeLegacyVault`, which validates the full legacy data and an independently cold-verified SQLite backup before a single transactional conversion. Migrated automatic execution is disabled.
- Historical v1/v2 tables are dropped only on known committed conversion and cannot mutate authoritative v3 data. The pinned historical executable is used in compatibility tests, including a warmed process attempting its real legacy SQL writes and old-reader refusal after conversion; it can still open the retained legacy recovery copy.
- The `tests/s3-binding` fixture retains the exact row-swap assertion, source-identity positive controls and bit-flip control, with added strict-purpose, version/header, import/rotation and cross-vault tamper cases. Explicitly record same-owner replay as a remaining cryptographic limit.
- Existing S1/S2, A3, B1/B2, 4 import, and policy tests remain hard CI gates. A strict v3 candidate is NOT beta-approved until the full exact-SHA Linux and Windows build, native storage, installer/artifact verifications pass and separate interactive Windows v1/v2 consent/upgrade/restore acceptance is recorded.
- No real vault was migrated, no protected ref changed and no stable/draft release was published. All testing uses disposable synthetic fixtures.
