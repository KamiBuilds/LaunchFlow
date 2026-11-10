# LaunchFlow architecture: ownership before folders (M0)

**Reviewed source pin:** `941c5f13bd993ffa18e0d883e77448379f7b54a5`, tree `3af53c0b048f0eac5f471df8cfa045bd687f3cf4`. The pin is not automatically revised by documentation-only commits. Source basis: targeted inspection of actual current `MainWindow`, `AutomationEngine`, `ConfigStore`, `ConfigurationSaveTransaction`, `CredentialVault`, `TeachRecorder` files, `AGENTS.md`, and current CI YAML. An inventory of the remaining files is recorded separately as provisional until read.

## Current dependencies (observed)

```text
App.xaml / MainWindow.xaml + MainWindow*.cs (WPF views and event handlers)
    |-- WorkflowConfigurationService, ConfigurationSaveTransaction, ConfigStore
    |       +-- VaultDatabase (encrypted configuration + durable writes)
    |-- CredentialVault --------------------------> VaultDatabase
    |-- AutomationEngine (RunAsync, login, browser and steps)
    |       +-- LoginHealth, WebsiteLoginMode, policy and workflow models
    |       +-- Playwright page/context ownership and browser profile services
    |-- TeachRecorder / modeless Repair dialog
    |-- Startup, lock policy, password session, profile sealing, tray and hotkey
    +-- diagnostics/localization and native Windows adapters
       Tests extract some production methods into platform-specific harnesses.
```

The current `MainWindow.xaml.cs` is a WPF/application coordinator with configuration saves, run control and Exit handling. `MainWindow.Execution.cs` owns ordinary Run, Teach and Repair event paths. `MainWindow.VaultLocking.cs` owns a task-sharing close/lock path, while installer shutdown already calls that path before Close. The ordinary Exit/OnClosed route is not equivalent.

## Target modular-monolith responsibilities

```text
WPF view/event handler: capture user intent; bind; show a typed outcome
     |
     v
Application operation coordinator: admits one valid owner, captures revision/session
     |                   |
     v                   v
Pure workflow/edit rules  |  Existing external adapters
 (scope, stage, role,      |  (Playwright/browser, encrypted vault,
  validity, state)        |   profile sealing, OS session, installer)
     +-------------------+
     v
Structured durable / lifecycle / runtime outcome, rendered by current WPF owner
```

Do not introduce a separate process, many new projects or generic interfaces per method. Incrementally extract a coherent service only when characterization tests show that the old boundary is causing bugs.

## Subsystem contracts

| Owner | Existing source | Responsible for | Must not own |
|---|---|---|---|
| Presentation | `MainWindow.xaml`, partials, dialogs | Accessibility, current selection, decisions, notifications | Encrypted transaction ordering, origin policy, browser teardown |
| Configuration/edit | `ConfigurationSaveTransaction`, `WorkflowConfigurationService`, `ConfigStore`, `AutoSaveSchedule` | Candidate/validate/durable commit/conditional publication | UI control discovery or optimistic saved labels |
| Credentials/vault | `CredentialVault`, `VaultDatabase`, credential guards | Secret storage, session authorization, configured secret roles | UI logging or unrelated preferences outside a coherent transaction |
| Login | `AutomationEngine.Login`, `AutoDiscovery`, `StagedLogin`, `LoginHealth` | Stage, binding, credential destination, completion/rejection evidence | Global form guesses or unowned retries |
| Execution | `AutomationEngine`, `AutomationEngine.Steps` | Run admission and ordered action outcomes | Modal notices as proof of success |
| Browser/profile | `AutomationEngine.Browser`, `ProfileVaultService` | Session, process and profile ownership; close and seal barriers | Cleanup of non-owned process |
| Teach/Repair | `TeachRecorder`, `VisualRecorderWindow`, WPF orchestration | Capture, full modeless lifetime and publish under original owner | Run overlap or late callback publication |
| Lifecycle | `ExitSafety`, session/Windows/startup adapters, WPF handlers | Lock grace, shutdown admission and awaiting cleanup | Silent exit on failed browser cleanup |
| Transfer | `SecureTransferService`, `LegacyMigration`, imports/exports | Detached parse, staging, compatibility/recovery | Implicit destructive reset |
| Distribution/diagnostics | `bootstrapper/`, `installer/`, `scripts/`, CI, diagnostics | Artifact identity, signing, trust, limited sanitized evidence | Claiming native/vendor acceptance from Linux simulation |

## Responsibility collisions — current source status

1. **A1 ordinary Exit:** source repaired via `ExitCompletionGate` + `MainWindow.Lifecycle.CompleteOwnedExitAsync`. Native WPF/OS-kill acceptance remains separate. Unexpected `OnClosed` without a permit stays best-effort.
2. **A2 modeless Repair:** source repaired — Run/Teach admission blocks stale modeless Repair; Teach uses `TeachUiOperationController`. Native WPF timing still pending.
3. **A3 credential/preferences save:** source repaired — `SaveCredentialAndConfiguration` for password-manager saves; website create uses the compound path when a password is supplied; credential clear uses `DeleteCredentialAndConfiguration` and resets AutoLogin/KeePass/LoginMode.
4. **Historical repository notes:** Treat `validated/v0.7.46-three-checks-20261103` (and later repair/structure branches) as development baselines. Do not silently recreate deleted branches.

## Extraction policy

- Characterize a collision with real production-linked RED/healthy controls before repair.
- Prefer new small policy/operation methods inside existing service boundaries before moving files.
- Keep mechanical path changes separate; update `csproj` linking, extraction scripts, UI resources and CI at every move.
- Every extraction must record current + target dependencies, ownership/lease handoff, compatibility risk and linked tests.
- Require independent review and exact-tree full gates before calling a slice complete. Native WPF/installer and authorized vendor sign-in are separate gates.

**M0+ status:** Controllers, WebsiteEditMapper, LoginFlowCoordinator, ExitCompletionGate, compound vault saves, TeachUiOperationController, and KISS guide coaching are in source. **v0.7.50** also places those owners under `UI/`, `Domain/`, and domain folders inside `Services/` (namespaces unchanged). See `LaunchFlow/STRUCTURE.md`. Native Windows acceptance remains the release gate.
