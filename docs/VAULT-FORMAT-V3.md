# LaunchFlow encrypted SQLite vault format v3

This page defines the **actual strict development layout**. It is an incompatible security format, not a migration of any real user vault or an authorization to release.

## Authoritative structures

- `secure_meta(id=1, vault_id BLOB, salt BLOB, verifier BLOB, argon_memory_kb, argon_iterations, argon_lanes, schema_version=3)`
- `secure_objects(id TEXT PRIMARY KEY, sealed BLOB)`
- `secure_credentials(workflow_id TEXT PRIMARY KEY, sealed BLOB)`

Both salt and vault ID contain 16 fresh random bytes. New vaults use Argon2id with 128 MiB, four passes and two lanes, and derive a 32-byte AES key. The actual parameters are recorded and authenticated indirectly through the verifier. The encrypted configuration is stored at object ID `config`; credential row IDs must be nonempty canonical GUIDs in lowercase 32-hex `N` format. Duplicate or noncanonical rows are rejected, not skipped.

The retired `vault_meta`, `objects` and `credentials` tables are **not authoritative in v3**. A successful conversion drops them atomically after writing strict records. The pinned historical executable cannot authorize v3 with its legacy verifier and cannot write the v3 tables through its original SQL; existing warm-key and cold-reader tests use the real retained code.

## Envelope and authentication

The strict sealed value is:

```text
ASCII "LFV3" (4 bytes) | CSPRNG nonce (12 bytes) | ciphertext (variable length) | GCM tag (16 bytes)
```

No fallback to legacy decryption is allowed for these strict records. The constant format header is checked before decryption and included in AAD. AES-GCM uses 256-bit keys and authenticates the associated data. The deterministic AAD layout is exactly:

```text
UTF8("LaunchFlow.Record") | 0x00 | UInt32-LE(3) | purpose byte |
vault_id (16 bytes) | Int32-LE(owner UTF8 byte length) | owner UTF8 bytes | ASCII("LFV3")
```

Purpose codes: verifier `1`, config `2`, credential `3`. Fixed owners: `verifier`, `config`, or the canonical expected workflow GUID from the authoritative destination row. The expected purpose/owner/vault ID come from the validated read/write operation, **not from the incoming ciphertext**. Reassigning an intact credential envelope to a different row or vault must fail GCM authentication before deserialization, session publication or credential use. New writes use fresh random nonces. Invalid envelope header, truncated ciphertext, altered tag or unexpected trailing bytes fail closed.

## Existing v1/v2 storage

- The normal runtime classifies legacy files without modifying schema and **does not unlock them** for ordinary use.
- The user must explicitly approve a one-way conversion and supply their old master password. The conversion authenticates original records with the legacy codec using a temporary key, never a published live session.
- A fresh same-database WAL-consistent SQLite backup is created in `LaunchFlow/recovery` under a collision-resistant unique filename. It is independently decrypted and compared with the source before any conversion writes. Backup failure stops conversion without rewriting the legacy vault.
- Conversion begins one SQLite transaction, rechecks the source, creates authoritative v3 records with a new salt and vault ID, disables imported workflows' automatic execution, removes old tables and commits before any key/cache publication. Cancellation before commit aborts. A late cancellation after commit leaves v3 locked rather than claiming rollback.
- Valid orphan credential GUIDs are preserved as orphan mappings. Empty, duplicate or corrupt inputs cause refusal. Policies forbidding credential storage block the conversion rather than discarding passwords.
- Old versions are supported only with the **untouched recovery snapshot restored into a separate location**. They cannot safely downgrade the upgraded file, and a pre-upgrade snapshot necessarily excludes later edits.

## Explicit limits and acceptance

AAD does not prevent replay of an earlier valid ciphertext for the *same* workflow, nor full database rollback without an independent trusted freshness record. Conversion cannot prove a legacy row association was untampered historically. Converted auto-run remains disabled pending owner review/re-entry of credentials; re-enabling is an explicit action. A malicious actor with arbitrary filesystem or SQL deletion rights is outside what AES-GCM AAD can prevent.

The retained original row-swap reproducer is preserved. The current fixture attacks the **authoritative** `secure_credentials` table and expands the tests across writes, imports, rotation, format headers, cross-purpose substitution and shared-key/different-vault contexts. The compatibility runner tests genuine pinned old code, a warmed older writer, strict reader and the preserved legacy backup.

No real-vault conversion, protected merge or publication is authorized. Native interactive Windows upgrade, consent, backup recovery, file-sharing and UI acceptance remain separate required evidence.
