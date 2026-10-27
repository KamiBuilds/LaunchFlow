# Both-3 profile compatibility and privacy

## Contract

- New seals again use the exact `origin/both` AES-GCM envelope: 12-byte random nonce, ZIP ciphertext, 16-byte tag, no header or additional authenticated data. The reference is `origin/both` commit `377dbbfb6313e2d6acd30dba9feef2e02b1e0602`. Its persisted-key and `Encrypt` methods are unchanged. An independent reader in the portable tests decrypts real `SealAll` output with this layout and the persisted key, then verifies the ZIP content; it does not call production encryption/decryption helpers.
- LFPS2 is **read-only compatibility support**, not the default format. Its unused production writer was removed. Independently constructed, authenticated, multi-chunk LFPS2 fixtures remain readable. Reading either format leaves ciphertext and `profile-key.dpapi` unchanged. There is no migration pass or key replacement. A later ordinary seal of an open profile writes the legacy snapshot; rollback readers still cannot directly read an existing LFPS2 package.
- Existing data directories, vault encryption, configuration and installer identity are unchanged. Missing or invalid profile keys fail closed; existing ciphertext is not overwritten without its key.
- Ciphertext sealing still stages to a unique file, flushes it, atomically publishes it, and only then deletes the source profile. Restores still extract to isolated staging and publish only a complete profile. Failures are aggregated, incomplete staging is removed, ciphertext is retained, and unwrapped keys are zeroed in `finally`.

## No plaintext ZIP spill

LFPS2 restoration now authenticates into a seekable **memory** ZIP buffer, not a `.zip-*` file, including on failure. Chunk buffers and the written ZIP buffer are zeroed in `finally`. The ZIP buffer is allocated once, with capacity capped by both ciphertext length and a **256 MiB decrypted-ZIP budget**, so `MemoryStream` growth cannot abandon earlier plaintext arrays. Each authenticated data chunk is checked against that budget before appending. A package over the limit fails with an explicit memory-limit error, publishes no profile, and keeps its exact encrypted file for recovery. There is no disk-spill fallback.

The budget covers ZIP bytes after decryption, not expanded entry content. Existing extraction limits remain separate: 200,000 entries, 4 GiB per entry and 8 GiB total, with traversal rejected by ZIP extraction. The large test uses a valid ZIP with a 257 MiB uncompressed synthetic entry and confirms limit rejection rather than an unrelated ZIP error.

**Tradeoff:** rollback-compatible AES-GCM writing restores the original whole-archive memory pressure (ZIP buffer, plaintext copy and encryption allocations). Legacy reading also retains its original whole-archive allocation behavior; the new 256 MiB bound is specifically for LFPS2 compatibility reads. This is not a streaming-encryption redesign or a claim that arbitrarily large legacy profiles are cheap. Large LFPS2 archives above the budget are deliberately not restored automatically; their ciphertext is retained. Managed-buffer zeroing does not guarantee erasure of OS paging, crash dumps, compression internals or every runtime copy. Profile protection is not containment against another process running as the same user.

## Verification

Run from the repository root:

```sh
/root/.cache/launchflow/dotnet/dotnet run --project tests/profiles/ProfileTests.csproj -p:TreatWarningsAsErrors=true
/root/.cache/launchflow/dotnet/dotnet build LaunchFlow/LaunchFlow.csproj -p:EnableWindowsTargeting=true -p:TreatWarningsAsErrors=true -p:PlaywrightPlatform=win-x64
```

The profile command includes the memory-budget test; `-- --budget-only` isolates that case. The harness links the actual production service. It uses only synthetic profile content and a clearly labeled randomized authenticated **DPAPI simulation on Linux**, not real Windows DPAPI or user credentials.

Evidence is retained outside the repository at `/root/projects/launchflow-evidence/both-3/profiles`:

- `baseline.log`: all 13 previous assertions passed before edits.
- `legacy-reader-red.log` and `legacy-reader-red.cs`: the new independent legacy reader failed with a real AES-GCM authentication-tag mismatch before any production edit. `legacy-reader-green.log` records the restored envelope passing.
- `plaintext-zip-red.log` and its fixture snapshot: the live filesystem observer caught the old LFPS2 restore creating a transient plaintext `.zip-*` file. The observer has an immediately deleted positive control, an event-drain barrier and fail-closed overflow/error handling; this is not merely a source assertion.
- `memory-budget-red.log` and its fixture snapshot: the valid over-budget LFPS2 archive was not rejected at a memory limit before the repair.
- `full-green.log`: **109 passing assertions**, including independent legacy read, legacy-write/old-reader compatibility, LFPS2 multi-chunk restore, data/tag tampering, missing/truncated termination, trailing data, reordered chunks, invalid lengths, invalid ZIPs, traversal, aggregate failures and healthy-profile progress. Failure tests assert no target, staging cleanup, ciphertext preservation, no plaintext ZIP creation and key zeroization.
- `strace-green.log` and `filesystem.trace`: the same 109 assertions passed under Linux filesystem syscall tracing. The only `.zip-*` creation was the observer's deliberate positive control; production created none.
- `app-crossbuild-win-x64.log`: application cross-build succeeded with zero warnings and zero errors. The first unrestricted copy attempt (`app-crossbuild.log`) ran out of disk while copying Playwright assets; only generated artifacts in this isolated worktree were removed, then the explicit Windows Playwright-platform cross-build passed. This is not an installer/publish gate.
- `verification.json` and `verify-evidence.py`: programmatically counted assertions, trace checks, source hashes and supplemental exact legacy-method comparisons.

Native Windows DPAPI/account binding, WPF execution, Windows filesystem watcher semantics, native fault injection and installer runtime remain separate verification gates. No release, push, real credential access or independent reviewer agent was used.
