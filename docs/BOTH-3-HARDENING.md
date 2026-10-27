# both-3 hardening

Base: `both-2` commit `ff3ef20bebd8fd49871e7d9dfa49232d4476cdc6` (0.7.20).
Status: implementation and verification in progress; not release-approved.

## Scope

Keep the UI refresh, workspace selection/preview, explicit login-success checks,
HTTPS-only automatic credentials, no-code readiness actions, redacted diagnostics,
browser download consent and signed-draft gate from both-2.

Repair the independently reproduced policy boundaries around continuation/submit,
initial navigation after window positioning, and blocked post-login redirects.
A late failed status cannot undo an unauthorized click; an allowed final URL must
not conceal a blocked intermediate document.

Restore profile writes compatible with the established AES-GCM envelope. Retain
read support for experimental LFPS2 profiles without writing a temporary plaintext
ZIP. This deliberately trades streaming performance for compatibility and privacy;
see the profile handoff for bounds and retained-file behavior once implemented.

## Acceptance status

- Policy dispatch regressions: pending integrated verification.
- Profile compatibility/privacy regressions: pending integrated verification.
- Full portable suites and warnings-as-errors Windows cross-build: pending.
- Fresh independent review of final integrated bytes: pending.
- CI on this branch: pending push and exact-SHA results.

## Native release gates (not covered by portable seams or packaging CI)

- Real Windows DPAPI, WPF rendering, keyboard/accessibility and high-DPI behavior.
- Physical display reordering/unplugging and actual browser window placement.
- Windows lock/unlock, sleep, browser crash and cancellation/lifecycle ordering.
- Clean Windows install, in-place upgrade with copied data, uninstall and rollback.
- Approved publisher signing and explicit human release authorization.

No stable release, original-branch replacement, installer identity change or vault
schema migration is part of this branch. End-user artifacts remain self-contained;
the SDK, Python, Inno Setup and test browser cache are builder-only requirements.
