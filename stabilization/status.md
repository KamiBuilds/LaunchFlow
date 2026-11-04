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
