# LaunchFlow Getting started: UI parity and visual provenance

**Source baseline:** `f08272a9dda410cb3f4bc2da5dc89cdcadb95ef2` (v0.7.46, source-level candidate; draft security PR #17).  
**UX review branch:** `ux/v0.7.46-one-action-guide-20261103` (draft; inspect final head before native verification).  
**Demonstrated Windows executable hash:** Not available. No native capture or installed-app visual acceptance has been performed.

## Visual inventory

The step locator in `TutorialWindow.xaml` is a **WPF-generated schematic based on source-verified labels**, not a screenshot of a running Windows application. It displays the window/section and one target label per step.

A user-initiated **Show target in app** (or **Open this area**) may safely switch to a matching MainWindow tab/category when the editor is clean, then outline a currently visible WPF element using its stable `x:Name`. It cannot click the element, change website selection, save settings, read credentials or execute automation. Dirty editors, open setup windows, or locked vaults refuse navigation and expand recovery text instead.

| Route | Step IDs | Current source UI | Locator / focus target |
|---|---|---|---|
| First website, no saved login | BASIC-01–13 | `MainWindow.xaml`, `NewWorkflowWindow.xaml` | `WorkflowsNav`, `AddWebsiteToolbarButton`, `NameBox`, `UrlBox`, `BrowserCombo`, `MonitorCombo`, `AutoLoginCheck`, `CreateWebsiteButton`, `WorkflowList`, `RunSelectedButton`, `SaveStatusLabel`, `SaveChangesButton` |
| Website credentials (optional) | PASS-01–08 | `MainWindow.xaml`, Settings → Passwords | `SettingsNav`, `PasswordsSettingsTab`, `PasswordWebsiteCombo`, `LoginModeCombo`, `UsernameBox`, `PasswordInput`, `SaveCredentialsButton`, `CredentialStatus` |
| Record clicks (optional) | RECORD-01–06 | `MainWindow.xaml`, `VisualRecorderWindow.xaml` | `WorkflowList`, `TeachButton`, `CaptureStatus`, `StepList`, `SaveStepsButton`; browser click is manually observed, not highlighted |
| Startup and background (optional) | STARTUP-01–05 | `MainWindow.xaml`, Settings → Startup | `SettingsNav`, `StartupSettingsTab`, `StartupCheck`, `UnlockCheck`, `TrayCheck` |
| Recover safely | HELP-01–06 | Main, browser-readiness dialog, startup recovery | `BrowserCheckAgainButton`, `WorkflowList`, `RepairButton`, `SaveChangesButton`, `RetryVaultLockButton`; HELP-06 is startup recovery (no live outline) |

## Content ownership and localization

The single maintained content source is `LaunchFlow/UI/Dialogs/TutorialWindow.xaml.cs` (39 step records). `docs/GETTING-STARTED.md` is generated from that source. `tests/ui/verify_guide.py` checks exact source labels, real target names, guide markup, and documentation parity. It must pass in CI.

English labels come from the v0.7.46 XAML and applicable code-behind. Runtime Dutch navigation changes `Settings` to `Instellingen`; the guide resolves that label using `LocalizationService`. Not every dialog is fully translated. A real native screenshot and completed localization parity review remain outstanding.

## Native operator verification — not yet performed

Use an authorized, clean Windows environment and synthetic workflows only. Confirm, and record the exact installed EXE hash and Windows build, at minimum: 100%/150% scaling; small resizable guide; keyboard navigation and Esc/Close; live target outline after resizing or scrolling; create-website dialog remaining usable with guide open; no focus theft while typing; opening/closing the guide repeatedly; MainWindow minimizing and Exit; browser readiness and an absent target; Settings localized to Dutch; and all credential inputs and user data absent from captures.

Review sanitized screenshots for identifiable titles, paths, URL/account data, taskbar details, logs and dropdown selections before sharing. If no authorized native operator is available, do not present these as passed.
