# Startup installer/build exit regression

Run from the repository root:

```sh
dotnet run --project tests/startup-exit/StartupExitTests.csproj -c Release
```

The build regenerates `obj/Production.g.cs` before compilation. It extracts the exact activation callback, full activation listener, vault initialization/recovery methods, reset-setup continuation, startup try/catch and MainWindow installer request. It includes `App.StartupExit.cs` and the unchanged fresh-recovery helper. `obj/source-binding.json` binds their original source bytes. `LAUNCHFLOW_SOURCE_ROOT` selects retained predecessor source without overwriting production.

The adapters replace WPF dispatch/window lifetime, SQLite, native dialogs and the final engine cleanup. Tests execute actual local named-pipe IO with the unchanged listener. They cover both exit commands and a non-exit control; loaded and unloaded shell save/refusal/retry; setup/unlock/recovery cancellation; repeat/reentrant initialization; cancellation during synthetic commit and constructor reentrancy. Synthetic byte arrays model preservation at the App boundary. These tests do **not** establish native WPF exit, actual encrypted SQLite preservation, browser cleanup, installer replacement or mutex release.

The 44 assertions also cover retained creation/orphaned sidecar warning cancellation and a reset initialization that unwinds to an older unloaded shell: its durable-save refusal remains authoritative and retryable. No test changes password text, validation, format, crypto or product paths. Native acceptance remains a separate Windows gate. The older handoff/recovery extracted-handler suites explicitly adapt the new cancellation dependencies under their no-exit scenario; their existing assertions run unchanged.

The installed native matrix is unchanged in size. `owned_wrong` now requires no-instance, setup-pending and unlock-pending exit controls; `recovery_cancel` also requires recovery-pending exit. Whole-root byte inventories include WAL/SHM without exemptions, apart from the two existing bounded diagnostics files. Portable source-linked PowerShell controls exercise this wiring, both process waits/exit codes, missing milestones and preservation refusals. They do not prove installed Windows execution. Native MessageBox/file-picker cancellation remains unverified; no generic Win32 cancellation layer was added. No-instance helper exit is not proof of a primary callback before its first window.
