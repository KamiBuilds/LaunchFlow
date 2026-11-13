# both-2 engineering handoff

Branch: `both-2`, based on `both`. Development work only; not a release candidate.

> Historical both-2 handoff. On `both-3`, profile writes and restore privacy are
> superseded by [BOTH-3-PROFILES.md](BOTH-3-PROFILES.md), and current acceptance
> status is in [BOTH-3-HARDENING.md](BOTH-3-HARDENING.md). New writes are legacy
> compatible; LFPS2 is read-only compatibility support with no plaintext ZIP spill.

## P0 — before release

| Area | Implemented | Remaining acceptance |
|---|---|---|
| Login success verification | Follow-up actions and destinations require a configured visible success selector and/or matching URL when AutoLogin is enabled | Native SSO/MFA browser tests; verify positive success conditions for every real workflow |
| Credential security | Automatic vault credential fields require HTTPS on the owning frame even without registry policy; generic Fill cannot fill password-type fields | Threat model, proxy/TLS interception review, authorize SSO origins, password autofill migration tests |
| Profile encryption | New LFPS2 authenticated 64 KiB streaming chunks; old AES-GCM envelopes still readable; integrity, traversal and multi-chunk tests | Native Windows DPAPI round-trip, multi-GB profile tests, crash/recovery tests, BitLocker policy |
| Workflow lifecycle | Preserves `both` cancellation/session barrier; new verification steps stop failed flows, no automatic destructive retries | Windows session lock/unlock, sleep, browser crash and repeated hotkey testing |

**Profile rollback warning:** LFPS2 writes are NOT readable by the old `both` binary. The older format is accepted by `both-2`, but the reverse is not supported. Do not test sealing real profiles without a verified encrypted data backup. This is a controlled, review-required profile-format upgrade, not a silent vault-schema migration. Credentials remain separately stored in the existing Argon2id/AES-GCM encrypted SQLite vault.

## P1 — capability improvements

| Area | Implemented | Further development |
|---|---|---|
| Teach Mode | Re-enabled click capture on form fields; structural selectors only, no values, labels or IDs | Selector uniqueness scoring, visual repair and resilient locator strategies |
| Workspace Manager | Windows usable work-area placement, stable monitor identity and fallback | Full drag-and-drop six-monitor designer, layout presets and Firefox native positioning |
| No-code authoring | `AssertVisible` and `WaitForURL` actions, with explicit failure reporting | Conditional branching, reusable steps and richer workflow visualization |
| Diagnostics | Explicit consent-based redacted support report, fixed event codes and automated privacy test | Local correlation IDs, user-accessible repair history and retention policy controls |
| Architecture | Split browser/profile/monitor context methods into `AutomationEngine.Browser.cs`; no replacement of the core execution barrier | Split MainWindow view model, orchestration and presentation more thoroughly |

## P2 — enterprise and verification

| Area | Implemented | Remaining gate |
|---|---|---|
| Enterprise delivery | Draft release now demands valid Authenticode signatures from a configured approved signing certificate; consent-only browser setup dialog | Sign artifacts using approved organizational certificate, test Intune deployments and production updates |
| Accessibility and localization | Expanded Dutch workflow editor, keyboard labels and existing navigation translations | Full localization audit, Narrator/NVDA, high contrast and 200% DPI |
| QA/release | Expanded profile tamper/multi-chunk, recorder privacy, browser action, monitor and diagnostics regression suites; Windows installer CI | Actual clean Windows runtime tests and signed installer upgrade/uninstall tests |

Development build artifacts are for testing only. CI cross-builds, browser fixtures and Windows packaging jobs cannot certify government deployment, privacy compliance or real Windows desktop behavior.

## Verification

Review the CI results for the exact head SHA, not an earlier successful commit. Main pipeline `.github/workflows/windows.yml` runs portable browser/profile/workspace/policy suites, native Windows build, Inno packaging, bootstrapper checks and artifact uploads. It does not publish stable releases automatically. A draft release is separately gated by explicit reviewer evidence and publisher signature verification.

User acceptance: test on copied vault and profiles; confirm no credentials were lost, approved pages display correctly, recording never saves typed secrets, monitor unplug/replug works, cancelled runs do not submit steps twice, and recovery after Windows locking does not reopen a sealed profile while it is being encrypted.
