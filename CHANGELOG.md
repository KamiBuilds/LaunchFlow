# v0.7.23 — Usability and privacy-preserving Teach recorder (development)

- **Website actions:** Ctrl/Shift multi-select, Select all, Delete selected (bulk deletion requires confirmation), edit one action by double-click, and a one-click Wait +1s action; wait steps can also target a selector for slow pages.
- **Teach recorder:** multi-select, edit, remove, Select all and explicit wait controls. Only pre-approved non-text navigation keys (Enter, Tab, Escape and arrows) may be recorded; text input, password/email field and password-containing form keystrokes are blocked. Typed searches and login credentials are NEVER recorded; use explicit Fill only for non-secret text and Passwords for encrypted logins.
- **Help:** the guide opens modelessly, allowing people to click the controls while reading instructions. Added annotated, privacy-safe example interface diagrams; these are illustrations, not screenshots of users or websites.
- **Save feedback:** status label changes to Unsaved changes after editing website details or action steps and to All changes saved after successful persistence.
- **Recorder usability:** restores the main LaunchFlow window to the foreground when Teach closes.
- **Maximize:** custom-window chrome restricts maximized bounds to the current monitor's work area to avoid covering taskbar and neighboring displays.
- **Slow pages:** click actions retain Playwright's normal locator auto-wait; explicit Wait-for-element actions can wait up to 60 seconds, and the editor explains both.

**Security constraints:** no typed text, emails, passwords, OTP or tokens collected by the recorder, no auto-screenshot capture of sensitive pages, no change to existing encrypted vault schema or profiles. Real Windows usability, DPI and multi-monitor acceptance still required before public release.

# v0.7.22 — Websites-first sidebar (development preview)

- Removed **Workspace Profiles** (saved-workspace selector, name field, Save/Preview/Run workspace buttons) from the visible interface.
- Renamed the main Workflows navigation and creation action to **Websites / New website**; the sidebar shows just the websites list, current status and website actions. Run all still launches enabled websites.
- Preserved previously saved workspace records in the encrypted configuration unchanged. A collapsed, non-interactive compatibility panel keeps the former WPF event and import contracts intact for now, preventing an unsafe storage migration just before release.
- Added UI contract assertions that the old panel is collapsed, not hit-testable or focusable, and its old visible labels are absent.
- Updated application/installer/CI versions to **0.7.22**. This remains an unsigned developer preview, not a stable release.

**Known limitation:** Workspace profiles are preserved but cannot be edited from the simplified UI. The user can continue working with individual websites. Restore legacy workspace management only in an explicitly designed future feature, if needed.

# v0.7.21 — No-login workflows, safer installer updates and workspace UI (development preview)

- New workflows default to **No login required**, avoiding incorrect MFA/sign-in prompts on local dashboards; saved-credential login remains an explicit choice.
- Added regression coverage for no-login pages whose content contains verification keywords.
- Made workspace Save/Preview labels fit the narrow sidebar.
- Added a graceful installer shutdown request and fail-closed cleanup before replacing application files. Previously installed builds may still need one manual close on their first update.
- Retained workspace import/revision validation, durable saves, encrypted-vault safety and regression gates from the preceding development branch.

**Development build policy:** Every new test installer delivered to the user gets a higher patch version (next build after v0.7.21 is v0.7.22). Multiple internal commits may share a version until that build is delivered; all version surfaces and artifacts must agree. A development preview is not a published GitHub release and will not appear in the in-app stable updater.

**Release status:** Experimental, not release-approved. Native Windows shutdown, UI scaling and real-profile restore/upgrade acceptance are still required.

# v0.7.20 — Workspace UI refresh and selection fixes (testing build)

- Redesigned the main window with clearer navigation, compact workflow actions, improved visual hierarchy and a consistent dark teal theme.
- Rebuilt the Workspace Profiles panel with a selectable saved-workspace dropdown and a separate field for naming workspaces.
- Fixed workspace selection, preview, save and launch behavior; running a saved workspace no longer depends on editable ComboBox text.
- Improved dropdown labels and popup presentation, workflow editor spacing, credential settings, and dialog consistency.
- Added in-app version indicators and improved the update-settings presentation.
- Preserved existing encrypted vault data, workflow IDs and the stable Inno Setup application identity.
- Added WPF XML, UI-control wiring and release-version checks to CI.

**Release status:** Experimental branch. The app only discovers updates from published GitHub Releases; changing the branch version alone does not publish an update. Verify native Windows UI behavior, installation and profile compatibility before releasing.

# v0.7.19 — Teal switches and multi-click recording

- Removed checkmark icons from checkbox controls, replacing them with compact on/off switches in LaunchFlow teal.
- Matched the workflow Enabled switch to the same blue-green accent.
- Record every user click, including form-field clicks, without collecting entered values or passwords.
- Register Teach listeners across browser navigations, child frames and newly opened tabs.
- Capture pointer clicks before redirects and keyboard-activated clicks as fallback.
- Continue showing the number of captured actions and give in-flight callbacks time to finish when saving.

**Testing:** Requires a native Windows compilation and practical Teach-session tests before release.

# v0.7.18 — Simpler interface and green enabled controls

- Checked boxes and the workflow on/off switch now use a consistent solid green.
- Removed the Activity page and hidden developer-only diagnostics from the user interface.
- Removed manual database backup/restore controls, first-run copies, periodic database backups and backup reminders.
- Kept password-protected Import and Export for moving websites between computers.
- Simplified security, update and export descriptions for everyday users.
- Kept existing encrypted vault data and files untouched during upgrades.

**Status:** Source validation only. Windows compilation and UI testing still required.

# v0.7.17 — KeePassXC setup and workflow toggle

- Replaced the Enabled badge with a compact switch.
- KeePassXC databases can be detected from familiar Windows folders, chosen from a list, or browsed.
- Added a searchable KeePassXC login picker in Passwords, without manually entering paths.
- Moved key file and manual entry path settings to Advanced.
- Only login names are displayed; no KeePassXC passwords are imported.
- Removed the manual KeePassXC path field from the New Workflow wizard.

**Status:** Source verification only. Test native Windows compilation and KeePassXC workflows before release.

# v0.7.16 — Independent website setup and local network URLs

- New Workflow remains visible when the main window is minimized.
- Add websites using HTTP, HTTPS, local IP addresses, localhost and custom ports.
- Preserve the protocol in pasted full URLs and warn about HTTP when saving credentials.
- Keep New Workflow modeless with one setup window at a time.
- The Windows builder requests shutdown of LaunchFlow before rebuilding and uses a forced close only as a fallback.
- Fixed the Playwright import for the Teach recorder.
- Preserved encrypted vault data and the existing Inno Setup AppId.

**Status:** Static verification required; Windows installation and runtime tests are still needed.

# v0.7.12 — Reliability and packaging cleanup (development)

- Fixed modeless Teach saving steps to the wrong website if the selection changes.
- Prevented overlapping Teach sessions and handled removal of a website during recording.
- Fixed the Playwright page-initialization script so Teach listeners run across navigations.
- Kept sign-in progress intact when opening Teach or Repair login on an existing browser page.
- Improved installer builder reliability: require an exact versioned Setup file and bundled .NET runtime dependencies.
- Clarified that end users need neither the .NET SDK nor Inno Setup.
- Preserved the existing installer AppId and encrypted vault location.

**Status:** Source checks only. A native Windows compile and real install/upgrade test are still required.

# v0.7.11 — KISS-rule usability and Teach clicks (experimental)

- Made Teach a movable non-modal window so the main app and browser remain usable.
- Start recording clicks automatically; live click updates survive normal page navigations.
- Save captured clicks when the user chooses Save clicks; never record typed input or passwords.
- Added a four-step getting-started guide, available anytime from Help.
- Simplified labels in Teach and the website setup wizard.
- Preserved the encrypted vault and existing installer ID.

**Testing:** Windows compilation and login-flow tests are required before merging.

# v0.7.10 — First-launch workflow display (development)

- Fixed a startup UI issue where the Workflows page stayed blank until switching to Passwords or Settings and back.
- Realize the headerless WPF TabControl after Loaded and reselect Workflows without triggering normal tab-change save/password handling.
- Populate the editor from the initially selected saved workflow; show the empty-state page only when there are no workflows.
- Updated application and installer versions to 0.7.10; the upgrade AppId and encrypted vault data stay unchanged.

**Build status:** Still requires a Windows compile and fresh-launch test before release.

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
