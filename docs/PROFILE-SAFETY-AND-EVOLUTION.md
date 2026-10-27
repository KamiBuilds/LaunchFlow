# Profile safety, compatibility and bounded-memory evolution

Development status: the default remains the established legacy AES-GCM envelope.
No new profile format, automatic migration or large-profile recovery utility is shipped.
Native Windows DPAPI, sharing/ACL, installed-browser and power-loss gates remain open.

## Current cleanup repair

A successful ciphertext publication used to be followed by recursive deletion of the
active profile folder. Deletion can partially succeed. Restore then skipped the remaining
folder, and ordinary resealing overwrote the complete encrypted snapshot with partial data.
This inherited sequence was reproduced against `25df42a`, `377dbbf` and `ff3ef20`.

The repaired sequence is:

1. Create the legacy ZIP/envelope, write a unique ciphertext temp, flush and publish it.
2. Atomically rename the source on the same volume to reserved `.sealed-<guid>` ownership.
3. Recursively delete only that disposal tree, never the authoritative workflow path.
4. Exclude `.sealed-*` and `.restore-*` directories and historical helper archives from
   normal sealing/restoration. Retain their bytes rather than promote or overwrite them.

If publication fails, the source remains. If quarantine rename fails, the source is still
intact and the complete published ciphertext remains. If recursive cleanup partially fails,
only the reserved disposal tree is partial; a retry cannot overwrite the complete ciphertext
from that tree. Normal restoration can recreate the workflow path from the retained archive.
The service reports incomplete cleanup; it does not promise that every plaintext file was
removed. Remnants may contain plaintext browser data and need support-led cleanup.

This is not secure erasure, OS containment, automatic reconciliation of old damaged folders,
or a power-loss durability guarantee. A same-user process with DPAPI access is not contained.
The native Windows rename/open-handle/ACL behavior must still be tested. Process-kill seams
were exercised separately from unmodified recovery; they do not establish storage-controller
or directory-entry persistence after machine power loss.

`tests/profile-cleanup/run.sh` reproduces actual Linux permission failures with tiny synthetic
profiles. The normal profile suite retains legacy-reader, LFPS2, tamper, traversal, zeroing,
no-plaintext-ZIP observer and explicit 256 MiB budget tests. DPAPI in these suites is simulated.

## Existing format and rollback boundaries

- Legacy writes retain `nonce[12] || ciphertext || tag[16]`, no new AAD or magic, and the same
  persisted `profile-key.dpapi`. The original reader can authenticate actual new output.
- LFPS2 remains read-only compatibility support. Its 256 MiB limit bounds the decrypted ZIP,
  not the expanded profile. Over-budget ciphertext is retained, not migrated or deleted.
- The legacy writer still buffers the whole ZIP and additional encryption arrays. This
  inherited, documented memory cost is unchanged; no bounded-memory claim is made for it.
- LFPS2 historically authenticates each record's ordinal and length but not a unique archive
  identity. Independent valid archives under the same key can supply accepted mixed records.
  A terminator and CRC are not a cryptographic archive binding. This inherited integrity
  limitation is unchanged and requires a release threat-model decision; do not restore LFPS2
  writing as a new streaming design or claim original whole-file provenance from its tags.
- An otherwise valid legacy nonce beginning `LFPS2` is currently misclassified and rejected.
  Its ciphertext is retained. A future bounded authenticate-only detector needs a regression
  before repair; do not bypass authentication or memory limits to recover it.
- Old applications do not understand reserved-helper ownership. Before rollback, preserve
  ciphertext/key backups and reconcile outstanding disposal/restore artifacts under support
  guidance. A readable envelope alone does not prove the entire old lifecycle is safe.

## Safe procedure for an existing LFPS2 budget failure

1. Stop automatic retries for the affected profile and close its browser cleanly. Do not
   delete browser coordination locks or create a replacement empty session.
2. With application writes stopped, make a private ciphertext-only backup of the complete
   `profiles-sealed` directory and original `profile-key.dpapi`; record exact names, sizes,
   whole-file hashes, application version and originating Windows account/machine.
3. Retain open profile and helper remnants separately under support guidance: they contain
   plaintext browser data. Never send them with ordinary diagnostics or treat them as healthy
   managed profiles. Old partial-delete damage cannot be inferred from folder existence.
4. Do not reset the vault, regenerate the key, rename LFPS2 to pretend it is legacy, edit
   private constants, use an old plaintext-ZIP-spilling reader, or automatically retry with
   an unlimited allocation. Saved vault credentials are separate from browser sessions.
5. Use a reviewed recovery build on the originating Windows account/environment only after
   explicit per-operation consent. DPAPI CurrentUser wrapping is not a portable recovery-key
   export. No such recovery build is supplied by this development milestone.

A future utility must open originals read-only, retain them even after success, preflight
finite compressed/expanded/metadata/record/disk/time budgets, authenticate complete framing
and EOF, and extract only to a side-by-side recovery directory outside managed enumeration.
Publishing into the active app is a separate consented transaction. Prefer a trusted preexisting
whole-file hash/backup when tampering is suspected: a newly computed hash identifies the input
but does not prove historical LFPS2 provenance.

## Proposed bounded-memory design (not implementation)

First address generation ownership/recovery independently of cryptographic optimization.
Keep the legacy writer as default. Existing source, target and encrypted snapshots need
explicit transaction identity and recovery state; mere directory existence is insufficient.
A future journal must be bounded, atomic, recoverable after interruption and unable to turn
untrusted helper paths into managed profiles. Do not delete the sole complete generation.

### Legacy-compatible streaming feasibility

ZIP creation can stream into an encryption sink, but .NET 8 `AesGcm` is a one-shot API, not
an incremental `CryptoStream` transform. True bounded-memory legacy writing would require
an independently reviewed incremental GCM implementation/provider preserving the exact old
nonce/ciphertext/tag layout, nonce discipline, final authentication and failure behavior.
Do not replace this with unauthenticated chunk writes or a hand-written crypto shortcut.
Full authentication must complete before publication or any source cleanup. Reading a ZIP
also needs bounded seekable access and metadata admission, not just a streaming writer.

### Explicit optional versioned container

If the reviewed provider cannot meet the legacy requirements, an optional future container
must authenticate its version/header, unique archive ID, workflow/profile identity, record
ordinal/length and completion totals. Derive separate per-archive keys with standard,
domain-separated derivation and enforce unique AEAD nonces. Bind every record and completion
to that same header so ciphertext-only cross-archive substitution fails. Use established
reviewed primitives, not a new cipher or an unreviewed proprietary crypto construction.

Use bounded plaintext buffers and encrypted scratch or a stable ciphertext seek adapter;
never write a temporary plaintext ZIP. Bound ZIP central-directory/index memory, record count,
entry count/name metadata, actual per-entry/total expansion, disk use and elapsed work
separately. Zero owned buffers on all handled exits without claiming forensic erasure of
managed copies, paging, dumps or compression internals.

LFPS2 recovery can use a bounded authenticated record scan and capped seek index, then
re-authenticate records as ZIP extraction seeks. Variable historical record sizes and a
stable encrypted input must be supported. This can avoid a whole-ZIP allocation without
changing production limits, but cannot retroactively strengthen LFPS2 archive identity.

### Migration and rollback acceptance

Migration must be explicitly selected, never triggered by ordinary lock/unlock. Preserve
original ciphertext/key, write a side-by-side candidate, independently authenticate/read it,
verify recovered content and publish only through the normal browser-close/generation barrier.
A crash or cancellation leaves an intact recoverable original. Rollback of current state
requires a fresh independently old-reader-validated legacy export; an old backup alone may
lose post-migration changes. Unknown/unsupported formats remain fail-closed and preserved.

Before any opt-in format or utility ships: demonstrate fixed working memory, controlled
above-256 MiB recovery, original-byte preservation, cross-archive tamper rejection, truncation,
resource exhaustion, traversal/collisions, disk full, cleanup/rename failure and interruption.
Then execute same-account native Windows DPAPI and filesystem/install/rollback gates and
obtain an independent review. No version bump, migration or release is authorized by this doc.
