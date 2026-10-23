# v0.7.9 — Integrated title bar and navigation (development)

- Consolidated Workflows, Passwords, Settings, and actions into the custom Windows title bar.
- Added minimize, maximize/restore, and close controls while retaining native window behavior.
- Introduced a Settings sidebar with a separate Updates section.
- Improved alignment of the workflow Enabled toggle.
- Updated app, Windows Setup, About dialog, and build/validation scripts to 0.7.9.
- Preserved the installer AppId and encrypted vault location for upgrades.

**Status:** Windows compilation and installation testing still required before a public release.

# v0.7.8 — Explicit credential saving and masked usernames

- Added **Save credentials** to the Passwords tab. It updates the encrypted vault only when clicked.
- Usernames/email addresses are now masked during entry, alongside passwords, with **no Show/Hide option**.
- Previously saved usernames and passwords are never populated into the visible editor.
- On existing credentials, an empty username or password field retains the old value. Nonempty fields replace saved values.
- Leaving the tab or using the main Save button no longer silently overwrites login details.
- Updated the New Workflow wizard to mask username/email entry too.
- Kept database schema, encryption, installer AppId, upgrade location, and workflow IDs unchanged.

# v0.7.8 — Windows build fixes

- Fixed the ambiguous WPF `Color` reference (`CS0104`) in `DarkWindowChrome.cs`.
- Explicitly discarded the two intentional asynchronous dispatcher operations (`CS4014`) in `App.xaml.cs`.
- Corrected the nullable-dictionary type passed to Playwright CDP (`CS8620`) in `AutomationEngine.cs`.
- Fixed the Windows CI installer workflow's stale v0.7.3 output file references.
- Kept the same Inno Setup AppId, database location, and vault format to support in-place updates.
- Added `tools/Verify-LaunchFlow-Source.py` for source/configuration regression checks.

**Verification:** Static checks run in the preparation environment. The Windows WPF compiler, installer, and running application must still be tested on Windows.

# v0.7.5 — Restore, login pause, uninstall wipe choice

- **Restore from backup** — Settings → Security replaces the live vault with a `launchflow-*.db` copy (safety copy kept first)
- **Open `.launchflow` from Explorer** — registered file type and second-instance pipe deliver the path into Import
- **Broken login pauses steps** — MFA/SSO/password-still-visible stops further steps for that site, tray warning, offer Repair login
- **Uninstall keep vs wipe** — Inno asks whether to delete `%LOCALAPPDATA%\LaunchFlow` (No = keep vault, recommended)

# v0.7.4 — Dark checkboxes, tighter editor, Authenticode path

- Custom dark Fluent-style checkboxes (teal check, focus ring)
- Tighter workflow editor spacing and shorter action labels
- Hardened Authenticode: `tools/Sign-LaunchFlow.ps1`, Inno `SignTool=lfsign`, `tools/New-CodeSigningCert.ps1`, `CODE-SIGNING.md`
- Builder reports signing status; signs app + setup/uninstaller when a cert is configured

# v0.7.3 — Icon + dark scrollbars

- Original LaunchFlow app icon (teal flow → monitor) for window, Start Menu, and tray
- Dark Fluent-style scrollbars app-wide (no light Windows chrome)
- Dark Windows title bars via DWM immersive mode
- Darker PasswordBox / ListBox item hover-selected states

# v0.7.2 — Release channel, repair login, click capture

- GitHub Actions **Release Windows installer** publishes `LaunchFlow-Setup-*.exe` to a GitHub Release (tag `v*`)
- Optional signing secrets: `LAUNCHFLOW_SIGN_PFX_BASE64` + `LAUNCHFLOW_SIGN_PFX_PASSWORD`
- **Repair login** + post-login health heuristics (MFA/SSO/password still visible)
- Visual recorder **live click capture** (skips password fields)
- Windows Hello periodic password re-auth (30 days)
- Keyboard focus ring on buttons; more AutomationProperties
- Builder opens setup only with `-LaunchSetup`; finds any `LaunchFlow-Setup-*.exe`

# v0.7.1 — Hardening pass

- Vault reset, Hello unlock, sealed profiles, Task Scheduler, backups, update check hooks

# v0.7.0 — Consumer-ready shell

- About, welcome, tray toasts, consumer installer copy
