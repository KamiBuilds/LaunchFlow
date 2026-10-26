# `both` integration checklist

This is a development branch, not a release or a promise of native Windows readiness.

## Source selection

- Repair foundation: `hermes` at `c3451c26208f75f3fb57924e3dfe1db8cfc8985e`.
- Feature source: `smartest-app-developer` at `cecb1776b2825d5b5ffe991b9fc631c3d34f8d8c`.
- Preserve the existing product, encryption/database formats, workflow identifiers, profile locations and installer identity.
- Integrate features selectively. A full-tree replacement would reintroduce older profile, KeePass and recorder defects.

## Integration status

- **Included:** the repaired profile/vault/KeePass foundation, recorder lifetime/target handling, self-contained installer safeguards and cached-downgrade checks from `hermes`.
- **Included, awaiting independent final review:** document-bound username/password approval and insertion (`ae1373b`). Local real-Chromium tests cover document/frame replacement, delayed navigation, readiness, cancellation and positive SSO. These tests use synthetic credentials and routed pages, not real accounts.
- **Included:** a regression-tested initial stop-on-failed-step repair (`4faa258`), preventing subsequent actions after an uncertain browser state.
- **In progress:** named workspaces, stable monitor selection/fallback, English/Dutch UI, accessibility and asynchronous vault-lock UI integration.
- **In progress:** administrator controls and privacy-safe diagnostics, with enforcement below UI toggles.
- **In progress:** detailed step-repair results, execution status, SSO handling and cancellation/session-close lifecycle hardening.
- **Pending:** integration of those isolated workstreams, final policy/monitor/UI wiring, final combined regressions and an independent review of frozen combined source.

## Required acceptance gates

- Existing encrypted data remains readable; workspace/model additions round-trip older configuration without replacing IDs or formats.
- Failed or unknown steps stop that workflow and cannot trigger later clicks or destination navigation.
- Vault lock cancels active work and coordinates browser closure before profile sealing; a competing run cannot reopen a profile during closure.
- Administrator restrictions are checked at relevant engine, credential, transfer and recorder boundaries, not just by disabled buttons. They are application controls, not OS/network containment.
- Re-run runtime, profile, recorder, installer, bootstrapper-form and new feature tests from the final combined checkout.
- Verify formatting/whitespace, dependency audit, warning-free app/bootstrapper cross-builds, PowerShell parsing and actual Windows CI installer builds.
- Independently review the exact final source before declaring this integration approved.

## Remaining native/manual gates

Linux Chromium fixtures prove bounded browser behavior. Simulated Windows platform seams and cross-builds do not verify native WPF, Windows DPAPI, real monitor topology, clean-PC setup, upgrades, uninstall or signing/SmartScreen behavior.

Missing supported browsers still need an explicit consent-driven setup path. Do not claim that end-user browser setup is complete merely because development Chromium is installed in CI.

Pushes create test/build artifacts only. There is no automatic stable publication. An optional explicitly authorized draft is not a stable release, and this branch does not authorize publishing one.
