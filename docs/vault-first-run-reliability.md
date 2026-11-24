# First-run vault reliability

## Candidate and compatibility

This workstream starts from `0.8.3` at
`9b4a302c5cb3f413daadfe7eb728b26197c98723` on
`fix/v0.8.3-vault-first-run-reliability-20261123`.
The integrated follow-up candidate is **0.8.5** on the separate `fix/v0.8.5-vault-reliability-integration-20261124` branch. The original repair candidate was **0.8.4**, following the repository's patch-version
policy. It is not a stable release. No protected branch merge is authorized.

The existing encrypted format, derivation parameters, minimum password rules,
legacy compatibility, per-user data path, installer AppId and credential
boundaries remain unchanged. Existing databases must never be silently adopted,
replaced or removed. New helpers remain under their existing UI/service owners.

## Reproduced defects

1. **Setup/storage admission mismatch.** On the unchanged native Windows app,
   setup accepted a ten-character mixed-class input which storage refused with
   `ArgumentException`, `0x80070057`, in `VaultDatabase.InitializeStrict`.
   The shared strength assessment does not consistently enforce the storage
   minimum before returning `Acceptable`.
2. **Failed-creation residue.** A complete production-DLL probe passed a synthetic
   credential with an invalid empty identifier. The real credential writer
   rejected it before commit. Rollback retained a zero-table SQLite file;
   subsequent ordinary initialization refused its existing destination.
   This is a demonstrated lifecycle defect, not an observed disk failure or
   the cause of the separately reported VMware incident.

## Requirements and evidence gates

- Shared assessment, UI admission and defensive storage validation agree.
- Valid matching input keeps its exact snapshot; mismatch/cancel/rejection
  cannot publish authorization or pretend initialization succeeded.
- New creation is non-clobbering and retryable after an owned pre-commit failure.
- Existing, replaced, unrelated, committed and uncertain-outcome material is
  preserved through failures and cleanup. SQLite WAL/SHM and handle lifetimes
  are part of the creation/publication contract.
- Recovery explicitly preserves unknown material before any deliberate fresh
  start; authentication failure alone never grants replacement authority.
- Fixed phase diagnostics distinguish admission, persistence, publication,
  post-commit bookkeeping and disposal without values, password-derived hashes,
  real paths, raw messages, stacks or database contents.
- A blocked diagnostic sink cannot break a healthy operation.
- Regressions demonstrate baseline RED followed by candidate GREEN. Existing
  correct behavior has characterization controls, not invented defect claims.
- Full-project build, affected maintained suites and independent review must
  use the final integrated bytes. Source-linked UI adapters are not native WPF.
- Native Windows acceptance must cover original CMD build, actual installation,
  whole-payload identity, setup/main/full exit/cold unlock, invalid-input
  correction, wrong-password refusal and preserved recovery.

## Implementation ownership

- `Services/Vault/VaultDatabase*`: durable creation, key/cache publication,
  ownership and optional fixed phase checkpoints.
- `Services/Vault/PasswordStrength`: shared eligibility and guidance.
- `App` and `UI/Dialogs`: setup/recovery presentation and startup handoff.
- `Services/Vault/VaultRecoveryService`: preserved recovery file operations.
- `Services/Platform/StartupDiagnostics`: bounded fail-open diagnostics.
- `tests`: production-linked behavior, real encrypted storage, explicit native
  seams and fault scenarios; synthetic fixtures only.

## Verification status

The integrated candidate builds without an external compiler adapter and with
zero warnings/errors. Parent-run local verification completed successfully:

- All ten top-level gate commands exited zero, including nineteen maintained
  gates (policy 124 cases; storage integrity 49 cases).
- Creation/fault/concurrency checks: 66; real Linux permission controls: 5;
  erased-hook and uninstrumented storage controls: 4.
- Four production-linked setup/handoff/recovery/diagnostic executables passed
  their 42 grouped checks, including 2,000 generated assessment comparisons.
- The complete compiled production DLL created 102 independent fixture vaults
  (100 varied generated inputs plus two repeat-input controls). All selected
  owners reopened; unrelated input refused; salts, keys and verifiers were
  distinct; all invalid-input assessment/storage controls agreed.
- A separate full-production-DLL probe verified all twenty ordered creation
  phases, short-input refusal, existing-destination preservation and successful
  creation/reopen with the diagnostic sink blocked. Owned fixtures were removed.

These results bind the integrated local source/test snapshot, not native WPF
execution or a published release. Independent production review remains
incomplete: the provider rejected the reviewer request, so no product approval
is inferred from passing tests.

The native harness corrections, including conservative spawn accounting,
complete recovery-root preservation checks and creation-time Windows Job Object
assignment, passed bounded independent source review. Its ten portable
preparation checks passed; Windows-targeted C# compilation is compile-only.
Actual Windows PowerShell 5.1 mechanism preflight, original CMD/installer build
and all six installed-product scenarios remain unperformed. No candidate Windows
installer/UI acceptance is claimed yet.

## Separate unresolved incident

The reported clean-VMware failure with a valid-length password is not yet
reproduced. Its existing log names a containing initialization method, not
an exact throw operation. Passing controls and the reproduced short-password
failure do not prove its cause or resolution. This candidate must not be
represented as fixing that incident without matching evidence.

The previous native clipboard scenario was blocked by external driver ownership
checks, not an observed application password rejection. Its failed result and
its retry remain unverified app acceptance, not successful product tests.

## Safety for contributors

Run tests only under owned disposable fixture paths. Preserve existing user
vaults and unrelated services. Never upload credentials, UI trees, clipboard
values, vault/profile files or dumps as diagnostics. Report child failures and
incomplete suites directly. Publishing, signing, migration and release are
separate from passing a candidate's engineering checks.
