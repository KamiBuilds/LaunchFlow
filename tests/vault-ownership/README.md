# Complete-assembly vault ownership acceptance

This harness loads the **built complete application DLL** and its existing
dependencies under .NET 8. It does not replace production storage with stubs.
It is method-level acceptance, **not native WPF/installer/VMware acceptance**.

Build the application first. On Linux:

```sh
dotnet build LaunchFlow/LaunchFlow.csproj -c Release \
  -p:EnableWindowsTargeting=true -p:PublishReadyToRun=false
python3 tests/vault-ownership/run.py \
  --application LaunchFlow/bin/Release/net8.0-windows10.0.19041.0 \
  --results ./ownership-results
```

Set `DOTNET` or pass `--dotnet` when the SDK is not on `PATH`. On Windows use
a disposable test user/environment, never a live user's application data.
The host is deliberately a separately named executable, not a replacement app.

The runner owns a fresh temporary root, supplies an existing isolated XDG data
home, disables core/minidumps, captures bounded safe results and removes the
fixture tree after success, failure or its subprocess timeout. Do not point the
host directly at an existing database. The host rejects a nonempty fixture root
or one outside its temporary directory. Process interruption/power loss may
require removing an identifiable owned temporary fixture root afterward.

Coverage:

- 100 independently generated eligible inputs spanning boundary lengths,
  alphanumeric/letter/digit/passphrase patterns and actual non-ASCII input.
- Two additional creations with the same freshly generated control input,
  proving distinct random salts, derived keys and encrypted verifiers.
- Constructor/inspection/unlock do not create an absent database or ownership.
- Successful creation has the expected current encrypted schema/config rows.
- Owner unlock from a fresh storage instance works; unrelated generated input
  and the common run control refuse every other owner's vault.
- Invalid input is refused consistently by assessment and storage without
  creating a database.
- Lock revokes the live key; raw generated passwords, raw SHA-256 password
  bytes and derived key bytes are not found in retained DB/WAL/SHM content.

Passwords, raw salts/keys/verifiers and their comparison fingerprints stay in
memory; output contains booleans/counts and public assembly identity only.
Immutable managed strings cannot be promised to have been completely erased.
The `owner_cold_unlock` field means a new storage object after Lock; it is not
a new process. Full process exit/reopen is a separate native acceptance gate.

`execution.json` records the actual exit and fixture cleanup. `results.json`
contains every case and refusal control. A zero exit requires every creation,
uniqueness and rejection assertion. A build/setup/timeout failure is not a
product counterexample. Bind the reported assembly digest to the exact source
manifest used to build it; an assembly version alone is not source identity.
