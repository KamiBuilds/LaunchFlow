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
| M2 durability/lifecycle | **Open** | A1/A2/A3 source-level collision analysis in `architecture/overview.md` | New RED controls and repaired full runtime/Windows build |
| M3 controlled extraction | **Not started** | Target ownership/direction written | One bounded slice with consumer/link preservation and regression suite |
| M4 product/native | **Blocked by external acceptance** | CI uses simulated/production-linked browser/seams | Real install/upgrade, WPF lifecycle, accessibility, vendor login, multiday use |
| M5 final integration/delivery | **Not started** | Unpublished artifact is available for test | Exact-tree independent approval, native evidence, explicit release authorization |

## Priority defects and bounded next milestone

| ID | Severity / basis | Exact source condition | Required independent test | Completion state |
|---|---|---|---|---|
| A1 / AC27 | Critical lifecycle, source-only | `ExitFromTray` calls `Close`; `OnClosed` is `async void` and drains run/Teach/engine after WPF closure; installer does pre-close await | ordinary/tray Exit with delayed browser cleanup, dirty editor, lock race, failed close; require no premature process/window termination | **Open, not reproduced by an executable RED probe** |
| A2 / AC23 | High concurrency, source-only | retained modeless `SignInDecisionWindow` can schedule Repair while `RunEnabledAsync` owns a newer run; core Repair admission lacks run check | dialog A retained, new run B active, click Repair A; zero `SaveAll`, recorder acquisition, or mutation | **Open, no RED test yet** |
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

## Next bounded implementation slice (M2/A2)

1. Capture original entrypoints and exact run/Teach admission contract from `MainWindow.Execution.cs`, `MainWindow.xaml.cs` and modeless `SignInDecisionWindow`.
2. Add one test in the production-linked UI harness, with a real modeless callback schedule and an active run B. Require RED on the existing code; keep healthy idle Repair and no-op Later controls.
3. Centralize the admission check **before** `SaveAll`, and recheck original workflow/revision/vault generation after awaits. No selector, playback or product behavior change.
4. Run focused UI + workspace + policy + full real-Chromium gates. Require exact-tree Windows CI build before a checkpoint.
5. Only then address A1 awaited ordinary Exit and A3 compound save under separate RED/positive controls. Keep external native gates open.

This status is intentionally conservative. It distinguishes what can be built and proven in GitHub CI from what can only be accepted on an authorized Windows machine or live service.
