# Native Windows acceptance checklist (v0.7.48+)

CI and synthetic fixtures are **not** this checklist. Run on a clean Windows 10/11 x64 PC with a supported browser installed. Do not use production passwords.

## Install / identity

1. Install the packaged EXE (self-contained). Confirm no separate .NET SDK is required.
2. Confirm Start Menu / desktop shortcut opens LaunchFlow.
3. Note version in the title bar / About. Confirm AppId / per-user `%LOCALAPPDATA%\LaunchFlow` path.

## Vault create + unlock

4. First launch → Create vault with a strong master password (≥12, mixed). Confirm unlock.
5. Exit fully (not tray-only). Reopen → Unlock with the same password.
6. Optional: enable Windows Hello once; confirm unlock; disable again if testing only.

## First website (no login)

7. Getting started opens (first-run focus). Follow Add website → Test this site.
8. Confirm example.com (or your test URL) opens on the chosen monitor/browser.
9. Exit → reopen → unlock → website still present (name, URL, browser, screen).

## Passwords

10. Settings → Passwords → save a **fixture-only** username/password for one site.
11. Lock vault (or Windows-lock timeout). Confirm password fields are unavailable.
12. Unlock → confirm status “Encrypted credentials saved” without prefilling the secret.
13. Remove password → confirm AutoLogin / mode cleared and Run does not request that secret.

## Durability / recovery

14. Export a `.launchflow` portable backup with a separate export password.
15. (Optional lab only) Corrupt or rename `launchflow.db` → launch app → use recovery UI:
    - Restore from `%LOCALAPPDATA%\LaunchFlow\recovery\*.db`, **or**
    - Quarantine + import `.launchflow`.
16. Confirm broken file was moved aside, not silently deleted.

## Teach / Run / Exit

17. Record one safe click on a test page → Save actions → Test this site replays it.
18. Run all with one enabled site; Stop cancels cleanly.
19. Close to tray (if enabled) then Exit; confirm no hang and vault locks.

## Upgrade preserve (when testing an installer upgrade)

20. Install previous build → create vault + one site + credential → install newer build over it.
21. Unlock → site + encrypted credential survive. No AppId / path change.

## Pass criteria

- No silent vault deletion.
- No plaintext website password on disk (spot-check with a unique fixture secret).
- No hang on Exit with an open browser session (allow cleanup; Retry vault lock if shown).
- Record evidence: OS build, LaunchFlow version, installer SHA256, pass/fail per step.

**Not covered here:** live corporate SSO/MFA tenants, code-signing trust policy, multi-day soak.
