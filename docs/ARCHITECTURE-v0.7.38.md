# LaunchFlow v0.7.38 — Architecture and quality gates

This document describes the **implemented** boundaries, not a promise that LaunchFlow is fully decoupled.

## Composition root and dependency direction

- `MainWindow.xaml.cs` remains the WPF composition root and owns lifecycle wiring. It injects the encrypted configuration writer, URL policy, Task Scheduler and Run-key adapters into services.
- `MainWindow.*.cs` partials own WPF controls and presenters. No partial should reimplement policy decisions already extracted to a service.
- `Services/WorkflowConfigurationService.cs` validates website URLs and HTTPS login origins **before** invoking the injected durable configuration writer. It never reads WPF controls or credentials.
- `Services/StartupRegistrationService.cs` disables the unused Windows startup mechanism before enabling the selected one. Failure is propagated to the WPF caller, which can report it.
- `Services/SessionLockPolicy.cs` makes the default 10-minute threshold independently testable. It has no access to vault keys, Windows events, clocks, files or browser sessions.
- `Services/VaultDatabase.cs` remains responsible for encrypted database access. `ConfigStore.SaveDurable` is used where the UI requires a confirmed durable save.
- `AutomationEngine.*.cs` still uses a shared partial type to preserve browser session state. This is **not** equivalent to dependency isolation; work toward a discrete browser session manager is ongoing.

## Non-negotiable compatibility invariants

1. No automatic migration, reset or deletion of an existing encrypted vault.
2. No changes to workflow IDs, persisted configuration shape, profile directory, encryption parameters or installer AppId in an architectural cleanup.
3. Saving credentials remains an explicit separate action. Website or settings saves cannot silently overwrite them.
4. On Windows lock, automation pauses. With a default 10-minute threshold, an earlier unlock may reuse the session; an unlock at or after 10 minutes must require reauthorization. The vault must also lock at the threshold while Windows remains locked.
5. Cleanup failure must not force deletion of browser profiles. A failed session close blocks sealing, retaining user data.
6. Per-workflow running sessions must reuse an open Playwright page when safe. The default-browser open-only mode is an explicit exception because it has no tracked Playwright session; it must not be advertised as fully duplicate-proof.
7. A repeated Run while automation is running must not start a second concurrent run.
8. No recorder may capture raw typing, passwords or MFA secrets.

## Tests

The Windows CI pipeline runs:
- Python source and architecture contracts for partial-class method ownership.
- `tests/architecture/ServiceContracts.csproj` against the **actual**, source-linked services, without requiring Windows, a live vault or browser.
- Production-linked workspace and UI orchestration regressions, and vault/policy test compilation.
- A real isolated Chromium recorder regression suite.
- WPF application compilation and installer build.
- A SHA-256 manifest and a complete versioned file/line report.

## Remaining work before a production-quality release

- Replace shared `MainWindow` partial state with view models and explicit UI commands gradually.
- Separate the automation engine's tracked browser-session ownership from login orchestration.
- Add end-to-end tests for startup, tray restoration, duplicate prevention across restart, native session lock/unlock, app updates and upgrades with a preserved encrypted vault.
- Implement signing and reputation-aware distribution without circumventing enterprise application-control policy.
- Independent security assessment of credential persistence, browser session isolation and import/export boundaries.
- Real Windows accessibility and visual testing at multiple display scales.

**Release status:** This branch is a development build. Passing CI does not certify flawless behavior or justify a 10/10 claim.
