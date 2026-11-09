# LaunchFlow rescue contract

LaunchFlow is the active engineering priority. The retained validated baseline is `validated/v0.7.46-three-checks-20261103`, while `repair/vault-backup-integrity-20261105` is the isolated repair branch; `main` remains protected and untouched. Historical branch references were archived in [run 41685214053](https://github.com/KamiBuilds/LaunchFlow/actions/runs/41685214053). Do not restore, merge or recreate deleted refs without approval. Preserve installer identity, encrypted data locations/formats, workflow IDs, product identity and required licensing.

## Acceptance gates

- Preserve the encrypted-vault format, existing data locations, workflow IDs and installer AppId. Never access real user credentials during tests.
- Fix concrete behavior with executable regression tests, RED then GREEN; static source checks are supplemental only.
- Preserve the modeless Teach UI, but bind captured actions to the original workflow through awaits. Protect cancellation/closure/navigation/repeated-session lifetime.
- End users install one executable, not a developer SDK, Git, Python, Inno Setup or a .NET runtime. Ship a self-contained .NET payload and all application libraries/Playwright driver. Handle a missing supported browser through an explicit, consent-driven app setup rather than a manual SDK command.
- Remove redundant narration, obsolete TODOs and comments that merely repeat code; retain rationale, safety boundaries, compatibility warnings and useful API summaries. Do not mechanically strip every comment or weaken security to simplify code.
- Keep a clearly documented native Windows verification gate: Linux cross-compilation and behavioral helper tests are NOT WPF/installer runtime verification. Never label a stable public release tested without the actual native gate.
- Use separate Git worktrees for independent writers. Do not edit a sibling's files or integrate while they are still writing. Independently review final frozen bytes, then rerun gates in the integrated tree.
- Use only an explicitly authorized inference connection when necessary. Do not introduce new external providers, inference accounts, credential sharing or API-key dependencies.
- No automatic stable release, force push, main/develop merge, vault migration, service restart or signing-certificate purchase. Save passing work on a recovery branch and verify its remote commit; expose blockers plainly.

## Continuity

The authoritative remote development baseline is the validated branch above. A previously documented rescue machine used `/root/projects/LaunchFlow-rescue` and `/root/projects/launchflow-evidence`; those are historical paths, **not** assumed present in current tooling. Current architecture, inventory and acceptance ownership are tracked in `architecture/` and `stabilization/`; all CI claims must be tied to an exact commit/tree and GitHub run.

Other project automation is intentionally paused by the user's exclusive-focus instruction. Do not resume it until the user changes that instruction. Keep existing servers/services running. Project focus state and paused IDs are recorded outside the public repo at `/root/projects/launchflow-evidence/focus-state.json`.
