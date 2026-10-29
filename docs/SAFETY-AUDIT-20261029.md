# Bounded profile-safety repair on the latest preview

## Scope

- Reviewed upstream: `chatgpt-safety-hardening-20261028` at `7b4b888ee1188fd5d74ea89085131f468eeea9b6`.
- Repair branch: `hermes-safety-repair-20261029`.
- This is source stabilization, not a new user-facing installer or release. Development version metadata is unchanged.
- The user authorized reviewing the newest branch and implementing a bounded improvement. The collaborator branch and the existing `hermes-next` branch remain untouched.

## Confirmed defect and selected fix

The upstream profile service publishes a complete encrypted archive, then recursively deletes the active plaintext directory. Recursive deletion can fail after deleting only some files. The damaged directory remains authoritative; restore skips it because it exists, and a later seal can replace the complete archive with that incomplete generation.

The Linux filesystem fault regression fails against the exact upstream implementation: after the reported deletion failure and complete ciphertext publication, the damaged source is still present at the managed profile path.

This branch deliberately ports the isolated `hermes-next` quarantine repair, not a merge of its UI or session code:

1. Publish the existing complete AES-GCM archive as before.
2. Rename the source within the same profile root to a reserved `.sealed-<guid>` disposal name before deleting any content.
3. Exclude `.sealed-*` and `.restore-*` helpers from ordinary sealing/restoration enumeration.
4. Report incomplete cleanup rather than claiming the plaintext directory was retained intact.

A failed delete can leave disposal debris, but ordinary retry cannot overwrite the complete ciphertext with that partial generation. Restore can recover the complete profile into its normal path. Existing envelopes, keys, locations, workflow IDs and LFPS2 read compatibility are unchanged.

## Regression and evidence

```sh
DOTNET=dotnet bash tests/profile-cleanup/run.sh
```

The new maintained suite links the real production profile service. Its DPAPI wrapper is explicitly simulated; the permission fault, recursive deletion, renaming, ciphertext comparison and restore use actual filesystem/crypto operations.

- Exact upstream source: expected RED, exit 134 at the authoritative-path assertion. Core dumps are disabled.
- Repaired source: 10 assertions pass, including unchanged ciphertext on retry, complete restoration, and exclusion of helper directories and historical helper archives.
- The Linux regression is included in the portable GitHub Actions job. It is not a Windows test.
- Detailed baseline/candidate gates and independent reviews are external evidence under `latest-safety-audit-20261029`; this document does not pre-claim their results.

## Explicit limits and remaining priorities

- Quarantined remnants can contain plaintext browser data. They are not securely erased or automatically reclaimed; preserve support-led recovery/cleanup rather than treating them as ordinary profiles.
- Cooperative exclusive profile ownership and a successful browser-close barrier remain prerequisites. This change is not containment against hostile same-user processes.
- Native Windows rename/sharing/ACL, DPAPI, power-loss, physical monitors/DPI, WPF interaction and installer upgrade/rollback still need actual Windows acceptance.
- The rest of the existing `hermes-next` session-generation/lifecycle work is still separate. Passing this profile repair does not approve the complete combined application or all manual sign-in/recorder changes.
- The whole-archive legacy memory pressure and intentional LFPS2 read budget remain; no new format or migration was introduced to solve them.
- Large UI orchestration and stale handoff documents should be improved incrementally, after concrete data-preservation/security failures. Do not replace them with a broad rewrite or silently transfer an older scoped PASS to a newer commit.

No stable merge or release is authorized by this repair.
