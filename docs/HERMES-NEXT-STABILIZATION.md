# Stabilization contract — hermes-next

Base: `both-3` / `25df42a7e2ce844b47a716891e214283ff4c1983` (v0.7.20).
Working branch: `hermes-next`; the proposed target is a future v0.7.21 release candidate.
Do not bump versions or publish a release until separately authorized. Do not move
`both`, `both-2`, `both-3` or collaborator branches.

## Priorities and acceptance

1. **Lifecycle:** regression-test overlapping Windows lock/unlock, delayed timeout,
   cancellation, browser cleanup failure and admission. Preserve a short-lock
   unlocked vault; expire it at the configured threshold (default ten minutes).
   A suspended OS cannot execute an application timer: recheck elapsed time on
   the next available execution; native sleep/resume behavior remains a gate.
2. **Profiles:** retain the legacy AES-GCM writer, persisted key and read-only LFPS2
   support. Failed restore must retain exact ciphertext and never publish a partial
   target. Design bounded-memory evolution with explicit migration/rollback; do
   not ship an incompatible default or silently raise restoration budgets.
3. **Workspace UX:** exercise create/save/select/edit/preview/run/import/restart
   state with portable production seams, and provide explicit native mouse,
   keyboard, popup-hit-testing and scaling acceptance steps.
4. **Maintainability:** small behavior-preserving extractions only after regression
   coverage. Preserve meaningful security/concurrency comments; no broad rewrite.
5. **Authentication:** retain HTTPS-only, element/frame/document-bound credential
   fills, policy rechecks, success markers and cancellation/session ownership.

## Required evidence

Use synthetic data only. Observe failing regressions before security-sensitive
production changes; retain baseline and repaired results separately. Run strict C#,
engine, policy/vault, profile, recorder, workspace/UI, source/XAML, packaging and
version compatibility gates. `dotnet run` uses `-p:TreatWarningsAsErrors=true`;
Windows cross-builds use `-p:PlaywrightPlatform=win-x64` to avoid unused driver copies.
No portable test, XML check, CI build or cross-compile proves native desktop behavior.

Native Windows 11 install/upgrade, actual lock/sleep/shutdown, UI mouse/keyboard,
100/125/150/200% scaling, six-monitor/reconnect and real DPAPI remain release blockers
until observed on an authorized disposable Windows environment with copied data.
Preserve installer AppId, vault/credential formats, workflow IDs and user settings.

## Verified baseline

Both exact-SHA GitHub workflows completed successfully for `25df42a`:
- https://github.com/KamiBuilds/LaunchFlow/actions/runs/40507920206
- https://github.com/KamiBuilds/LaunchFlow/actions/runs/40507920110

These are build/regression/packaging evidence, not interactive Windows validation.
External task evidence: `/root/projects/launchflow-evidence/hermes-next/`.

## Progress

- Lifecycle milestone `62d2387` is independently reviewed and pushed: three stale
  unlock continuations are fenced by session generation/cancellation ownership;
  lifecycle orchestration moved into `MainWindow.Session.cs` without a broad rewrite.
- Profile partial-delete/reseal data loss was independently reproduced and repaired;
  the exact reviewed service bytes are integrated with a maintained permission-fault
  regression. See [profile safety and evolution](PROFILE-SAFETY-AND-EVOLUTION.md).
  Whole-suite integration verification is retained outside the repository.
- Workspace investigation confirmed four gaps: post-confirmation durable save,
  selected-ID persistence, pending editor changes in Preview, and null imported entries.
  Focused repairs are in progress; native dropdown behavior is still unverified.
- Native acceptance remains [unexecuted](NATIVE-WINDOWS-ACCEPTANCE.md). No version
  change, stable merge or GitHub Release is authorized.

## Final report

Report branch/base/final SHA and commits; implemented changes; discovered/unresolved
problems; exact commands, results and CI URLs; compatibility/rollback; requests for
ChatGPT's independent review; remaining native/release blockers. Passing artifacts
must be associated with their exact source commit, never transferred to a later tip.
