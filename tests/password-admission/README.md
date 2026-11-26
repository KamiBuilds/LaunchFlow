# New-password admission and legacy compatibility regressions

These developer tests link the real password assessment, vault creation/rotation,
portable export/import and recovery services. UI handlers run with explicit WPF
adapters, not a native window host. Inputs and encrypted fixtures are synthetic;
console output contains case IDs and outcomes, never submitted values.

Run from a clone with Python 3 and a .NET 8 SDK:

```sh
python3 tests/password-admission/run.py
```

Use `--dotnet /path/to/dotnet` when the SDK is not on PATH. The runner creates an
owned temporary data root, sets the test vault and data-directory environment
before starting .NET, preserves the child exit code and removes its fixtures.
Do not invoke the test executable directly or point it at real vaults.

For a predecessor replay, add `--source /absolute/path/to/source`. This selects
production links while retaining the exact current assertions. It must be an
owned source snapshot, not a user data directory.

## Coverage

- `POL-*`: numeric/padded/common-prefix and complete or truncated repeating
  patterns, including supplementary Unicode decimal digits. Repeating patterns
  use Unicode scalars rather than UTF-16 code units; no input normalization is performed.
- `UI-*`: actual setup/new-password rejection and unchanged existing-password
  prompts through portable WPF adapters.
- `DB-*`: real creation refusal before publication and refused rotation that
  retains the original cold-unlock owner.
- `EXP-*`: real portable-writer refusal before publishing a weak archive.
- `CTL-*`: healthy generated, spaced and Unicode passwords; exact raw input at
  independent cold reopen; wrong-owner refusal.
- `LEG-*`: independently constructed old vault/backup fixtures read and upgraded
  by real production consumers without new-password revalidation.
- `STO-*` and `REC-*`: durable-write refusal, failed creation cleanup, retained
  creation material and preservation-first recovery boundaries.

The executable writes fixed-ID case results beneath its own build output. A
nonzero exit or any missing case is failure. Linux/.NET 8 execution is verified;
this suite does not certify native WPF dialogs, Windows Hello, power-loss IO or
the separately reported VMware incident. The native installed-product workflow
remains a distinct acceptance gate.
