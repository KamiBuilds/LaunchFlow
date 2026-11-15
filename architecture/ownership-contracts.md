# Ownership contracts (keep; do not “simplify”)

LaunchFlow uses three independent ownership signals together. Losing any one of
them reintroduces stale-write and Run/Teach overlap defects.

## 1. Workspace / editor revision (`_workspaceRevision`, `WorkflowEditorSession.Revision`)

- Bumps when the live configuration graph or editor draft generation changes.
- Used to refuse Run/Teach/workspace/delete work that started against an older catalog.
- Does **not** prove the vault key or disk identity is still the same.

## 2. Vault session (`VaultDatabase.SessionGeneration`)

- Bumps on unlock, lock, master-password rotation, and other authorization turns.
- Passed into `SaveDurable(..., expectedSession)`, compound credential writes, Teach
  publication, and export capture.
- Rejects writes after lock/unlock even when the same `Configuration` object is still
  referenced in memory.

## 3. Instance identity (`ReferenceEquals` on `Configuration` / `WebsiteWorkflow`)

- Guarantees the UI is still talking to the same live object, not a same-GUID import
  replacement or detached clone.
- GUID equality alone is insufficient: imports can reuse IDs with new instances.

## Typical admission pattern

```text
capture: ownedConfig, ownedWorkflow, revision, vaultSession
await / modal / external process
recheck:
  !_exiting && !lockInProgress && vault unlocked
  ReferenceEquals(currentConfig, ownedConfig)
  ReferenceEquals(currentSelection, ownedWorkflow)   // when selection-bound
  revision == currentRevision                        // when catalog-bound
  vaultSession == VaultDatabase.Current.SessionGeneration
then: SaveDurable / engine dispatch / Teach publish
```

## Cross-writer disk identity (vault)

Independent `VaultDatabase` instances also compare an authenticated on-disk
identity token (`vault_meta` salt/verifier/params hash) before mutating. That is
orthogonal to UI session generation: it stops stale process-local writers after
rotation or foreign writes.

## Controllers that encode parts of this

| Type | Owns |
|---|---|
| `RunUiOperationController` | Run lease / stop / exclusive UI run admission |
| `TeachUiOperationController` | Teach/Repair lease across modeless lifetime |
| `WorkflowEditorSession` | Selection reference, edit revision, dirty ack |
| `HelloUnlockCoordinator` | Hello enable epoch + durable preference writes (vault unlock/session supplied by caller; coordinator stays vault-free for workspace stubs) |
| `ConfigurationSaveTransaction` | Validate → durable write → conditional publish |

Do not collapse these into a single “context” object without characterization
tests for every former refusal path.
