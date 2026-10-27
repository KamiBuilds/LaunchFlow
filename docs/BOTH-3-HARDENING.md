# both-3 hardening

Base: `both-2` commit `ff3ef20bebd8fd49871e7d9dfa49232d4476cdc6` (0.7.20).
Status: integrated local gates passed; fresh independent review and exact-commit CI pending. Not release-approved.

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
see [the profile handoff](BOTH-3-PROFILES.md) for the 256 MiB LFPS2 read budget,
legacy whole-archive memory tradeoff and retained-file behavior.

## Acceptance status

- Policy dispatch: all nine reviewed counterexamples now pass maintained assertions;
  the full runtime suite passes with warnings treated as errors.
- Profiles: 109 assertions pass, including independent legacy-reader compatibility,
  LFPS2 reads, live plaintext-ZIP observer, corruption and memory-limit failures.
- Recorder: 99 behavior checks pass with real Chromium and production replay.
- Workspaces/UI: 8 linked-production and 28 extracted UI-method checks pass.
- Administrator policy/vault: 117 checks pass; baseline/new/baseline encrypted
  configuration and vault compatibility passes (separate from profile archives).
- Diagnostics, UI/source contracts, installer 69 checks, bootstrapper form 5 checks,
  packaging 62 checks and PowerShell parser/builder/workflow checks pass.
- Actual app and bootstrapper Windows cross-builds: zero warnings and errors.
- Fresh independent review of integrated production bytes: pending.
- Exact-commit Windows packaging CI: pending push and results.

### Fixture corrections and evidence bounds

The first combined runtime run and a sequential retry failed before application
execution because the new fixture navigation inherited a 2.5-second action timeout.
Navigation setup now has its own 30-second deadline; action timeout, policy
revocation boundary and all assertions remain unchanged. The next run exposed
an existing loopback-server cleanup exception after listener disposal; teardown
now publishes its stopping state before closing and suppresses only expected
shutdown exceptions. The subsequent complete runtime suite passes. These were
fixture repairs, not weakening of production checks; original failures are retained.

Local evidence: `/root/projects/launchflow-evidence/both-3/final-validation`.
The initial bootstrapper command used a nonexistent project path; its corrected
`bootstrapper/LaunchFlow.UniversalInstaller.csproj` strict build passes. Windows
cross-builds select `PlaywrightPlatform=win-x64` to avoid copying unused Linux/macOS
driver payloads into a Windows builder's output. No native application execution
or actual installer lifecycle is established by these checks.

## Native release gates (not covered by portable seams or packaging CI)

- Real Windows DPAPI, WPF rendering, keyboard/accessibility and high-DPI behavior.
- Physical display reordering/unplugging and actual browser window placement.
- Windows lock/unlock, sleep, browser crash and cancellation/lifecycle ordering.
- Clean Windows install, in-place upgrade with copied data, uninstall and rollback.
- Approved publisher signing and explicit human release authorization.

No stable release, original-branch replacement, installer identity change or vault
schema migration is part of this branch. End-user artifacts remain self-contained;
the SDK, Python, Inno Setup and test browser cache are builder-only requirements.
