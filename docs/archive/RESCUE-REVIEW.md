# `hermes` cleanup and regression review

Source baseline: `chatgpt` at `0df5185` (v0.7.19). Changes belong on `hermes`, not on `chatgpt` or `main`. This is an engineering checkpoint, **not a stable-release approval**.

## Corrected behaviors

- Recheck the current origin after a username/continue step before filling a password. A workflow without a separately configured password origin must not bypass this check. An explicitly approved SSO origin remains supported.
- Ignore hidden password inputs when detecting an unfinished login. An inspection failure requires attention rather than reporting successful sign-in.
- Reject a busy browser profile. Never delete a running browser's coordination files or copy its live SQLite databases into an improvised recovery profile.
- Preserve exact KeePass username/password whitespace and empty usernames. Explicit `show -a` output is raw attribute data, not a labeled summary. Ambiguous multiline output is rejected.
- Pass KeePass arguments through `ArgumentList`, preserve quoting/trailing backslashes, drain both child output pipes while waiting, and never echo raw child output into a credential error.
- Persist a random browser-profile key wrapped with Windows DPAPI. Hashing a fresh `ProtectedData.Protect` result on each operation did not reproduce the encryption key.
- Restore profiles into an owned staging directory and publish only a complete extraction. Report failures, preserve existing ciphertext, and block replacement browser creation after a failed restore.
- Remove duplicate ignore rules and obsolete destructive recovery helpers; keep comments explaining trust, ownership, compatibility and error boundaries.

## Browser-profile compatibility warning

The encrypted credential-vault schema, workflow IDs and data locations are unchanged. New browser-profile snapshots retain the AES-GCM envelope and use `profile-key.dpapi` in the existing application data directory. **That file must be retained with the sealed profiles.** It is account-bound, not portable.

The previous profile-key implementation discarded randomized DPAPI ciphertext. Already sealed legacy profiles without a saved key cannot be reconstructed by this repair. Do not imply that those sessions have been recovered, invent a replacement key, or overwrite their ciphertext. This branch reports the problem and blocks affected sessions. Saved LaunchFlow vault credentials are separate. Any recovery/reset procedure must preserve the original files and be chosen explicitly by the operator.

Profile sealing is not the same as vault-password encryption. It does not contain other processes running as the same Windows account, an administrator, or malicious browser code within an already approved origin. A sealing failure may leave the original plaintext profile in place; the caller receives a failure instead of a false success.

## Reproducible development checks

Use a development .NET 8 SDK; this is not an end-user installation step.

```sh
dotnet build LaunchFlow/LaunchFlow.csproj -c Release -p:EnableWindowsTargeting=true
dotnet run --project tests/runtime/RuntimeTests.csproj
dotnet run --project tests/profiles/ProfileTests.csproj
```

The runtime suite needs the Chromium and Chromium headless-shell revisions matching Microsoft.Playwright 1.63.0. Set `PLAYWRIGHT_BROWSERS_PATH` if using an isolated development browser cache. Network traffic in the login tests is fulfilled by local Playwright routes, not sent to a real sign-in site. KeePass subprocess tests use an owned shell fixture on Unix; they do not unlock a real KeePass database. Tests restore their environment and remove only their owned temporary directories.

The profile suite executes the production profile service with a **simulated randomized authenticated DPAPI contract** and an isolated data directory. It proves round-trip/key-persistence and filesystem error behavior under that contract, not native Windows DPAPI compatibility. The real Windows account-bound API remains a native gate.

The initial browser-profile contention probe incorrectly used Chromium headless shell, which did not establish normal singleton coordination. Those failed harness logs are retained separately. The corrected probe uses full Chromium (`Channel = "chromium"`), requires real Linux singleton files, reproduces the baseline defect and passes after the repair.

NuGet's resolved direct/transitive package audit reported no known vulnerable packages from the configured source during this checkpoint. This is not a general security certification.

## Required before a stable release

- Independent review of the final integrated bytes, then rerun regression/build/formatting and package gates.
- Native Windows 10/11 x64 WPF tests: startup/unlock, Teach lifecycle, tray/hotkey behavior, cancellation and visible error dialogs.
- Native DPAPI and real browser profile lock/unlock across process restarts, including legacy/missing/corrupt key paths and preservation of existing user data.
- Native setup, upgrade and uninstall in a clean disposable Windows account without an SDK or preinstalled .NET runtime.
- Complete and exercise explicit, consent-driven setup when a supported browser is missing. Detecting an installed browser or printing a manual SDK/browser-install command is not that gate.
- Keep stable publication default-deny. A cross-build, simulated OS seam, or successful artifact upload is not proof of native installer/app readiness.
