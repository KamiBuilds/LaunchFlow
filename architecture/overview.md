# LaunchFlow architecture: ownership before folders

**Current tree:** LaunchFlow v0.7.89 modular monolith under `LaunchFlow/`.
Folders document ownership; namespaces stay `LaunchFlow*`. Native Windows
interactive acceptance remains the release gate — helper tests are not that gate.

## Current dependencies (observed)

```text
App.xaml / MainWindow.xaml + MainWindow*.cs (WPF views and event handlers)
    |-- Controllers: RunUi / TeachUi / WorkflowEditorSession / HelloUnlock
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

See also:

- [`ownership-contracts.md`](ownership-contracts.md) — revision + vault session + `ReferenceEquals`
- [`mainwindow-map.md`](mainwindow-map.md) — MainWindow partial responsibilities
- [`automation-engine-map.md`](automation-engine-map.md) — AutomationEngine partial map
- [`../LaunchFlow/STRUCTURE.md`](../LaunchFlow/STRUCTURE.md) — on-disk layout

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

Do not introduce a separate process, many new projects or generic interfaces per
method. Incrementally extract a coherent service only when characterization
tests show that the old boundary is causing bugs.

## Subsystem contracts

| Owner | Existing source | Responsible for | Must not own |
|---|---|---|---|
| Presentation | `MainWindow.xaml`, partials, dialogs | Accessibility, current selection, decisions, notifications | Encrypted transaction ordering, origin policy, browser teardown |
| Configuration/edit | `ConfigurationSaveTransaction`, `WorkflowConfigurationService`, `ConfigStore`, `AutoSaveSchedule` | Candidate/validate/durable commit/conditional publication | UI control discovery or optimistic saved labels |
| Credentials/vault | `CredentialVault`, `VaultDatabase`, credential guards | Secret storage, session authorization, configured secret roles | UI logging or unrelated preferences outside a coherent transaction |
| Login | `AutomationEngine.Login*`, `AutoDiscovery`, `StagedLogin`, `LoginHealth` | Stage, binding, credential destination, completion/rejection evidence | Global form guesses or unowned retries |
| Execution | `AutomationEngine`, `AutomationEngine.Steps` | Run admission and ordered action outcomes | Modal notices as proof of success |
| Browser/profile | `AutomationEngine.Browser`, `ProfileVaultService` | Session, process and profile ownership; close and seal barriers | Cleanup of non-owned process |
| Teach/Repair | `TeachRecorder`, `VisualRecorderWindow`, WPF orchestration | Capture, full modeless lifetime and publish under original owner | Run overlap or late callback publication |
| Lifecycle | `ExitSafety`, session/Windows/startup adapters, WPF handlers | Lock grace, shutdown admission and awaiting cleanup | Silent exit on failed browser cleanup |
| Transfer | `SecureTransferService`, `LegacyMigration`, imports/exports | Detached parse, staging, compatibility/recovery | Implicit destructive reset |
| Distribution/diagnostics | `bootstrapper/`, `installer/`, `scripts/`, CI, diagnostics | Artifact identity, signing, trust, limited sanitized evidence | Claiming native/vendor acceptance from Linux simulation |

## Extraction policy

- Characterize a collision with real production-linked RED/healthy controls before repair.
- Prefer new small policy/operation methods inside existing service boundaries before moving files.
- Keep mechanical path changes separate; update `csproj` linking, extraction scripts, UI resources and CI at every move.
- Every extraction must record current + target dependencies, ownership contracts and linked tests.
- Require independent review and exact-tree full gates before calling a slice complete.

**Status:** Controllers, WebsiteEditMapper, LoginFlowCoordinator, ExitCompletionGate, compound vault saves, TeachUiOperationController, HelloUnlockCoordinator, and domain folders under `Services/` are in source. Native Windows acceptance remains the release gate.
