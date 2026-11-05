# LaunchFlow strict vault format v3 — implementation contract

S3 is a format change, not an in-place tweak of the v1/v2 AES-GCM envelopes.

- **New authoritative tables:** `secure_meta`, `secure_objects`, `secure_credentials`. The legacy `vault_meta`, `objects` and `credentials` tables are never authoritative in v3.
- **Metadata:** format version 3, random 16-byte vault ID, Argon2id salt and parameters, authenticated verifier. Metadata format selection is structural and must be validated before key publication.
- **Envelope:** 4 bytes ASCII `LFV3`, 12-byte random nonce, AES-GCM ciphertext, 16-byte GCM tag. No trailing or optional fields. The 4-byte header is included in authenticated associated data.
- **AAD:** deterministic binary encoding of `LF-RECORD`, big/little endian fixed version integer, purpose, persistent vault ID and canonical record identifier. The expected identifier is the requested configuration key or canonical workflow GUID (32 lowercase hex digits, `N` format). For verifier, use the fixed verifier identifier.
- **Domains:** verifier, configuration object and credential use distinct purpose IDs. No raw mutable display text is an identity.
- **Compatibility:** v1/v2 are recognized without mutation and offered an explicitly consented, verified-backup one-way upgrade. New v3 data is never opened or written by legacy codec. The preserved recovery snapshot remains legible only to an older compatible app; rollback means restoring that snapshot separately, not opening the v3 file in an old executable.
- **Security limits:** Associated data rejects reassignment of an intact credential envelope to another workflow or vault context. It does not prevent a valid same-workflow ciphertext replay, a full valid database rollback, arbitrary database deletion, or historical tampering that happened before legacy upgrade. Migrated auto-run is disabled until credential associations are reviewed.
- **Authorization:** Neither a release nor a real-vault migration is authorized by source/test work. New format requires fresh native Windows acceptance before beta.
