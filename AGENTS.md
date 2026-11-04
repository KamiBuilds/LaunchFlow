# LaunchFlow rescue contract

LaunchFlow is the user's active engineering priority. The user-approved surviving working branch is `validated/v0.7.46-three-checks-20261103`; `main` is deliberately untouched. The previous `hermes`, `chatgpt`, `both` and other historical branches were removed in the authorized cleanup after Git history and branch refs were archived in [run 41685214053](https://github.com/KamiBuilds/LaunchFlow/actions/runs/41685214053). Do not assume those refs still exist or silently restore or merge them. Preserve product identity, existing installer AppId, encrypted data locations/formats, workflow IDs and licensing; do not invent a license or a new product.

## Acceptance gates

- Preserve the encrypted-vault format, existing data locations, workflow IDs and installer AppId. Never access real user credentials during tests.
- Fix concrete behavior with executable regression tests, RED then GREEN; static source checks are supplemental only.
- Preserve the modeless Teach UI, but bind captured actions to the original workflow through awaits. Protect cancellation/closure/navigation/repeated-session lifetime.
- End users install one executable, not a developer SDK, Git, Python, Inno Setup or a .NET runtime. Ship a self-contained .NET payload and all application libraries/Playwright driver. Handle a missing supported browser through an explicit, consent-driven app setup rather than a manual SDK command.
- Remove redundant narration, obsolete TODOs and comments that merely repeat code; retain rationale, safety boundaries, compatibility warnings and useful API summaries. Do not mechanically strip every comment or weaken security to simplify code.
- Keep a clearly documented native Windows verification gate: Linux cross-compilation and behavioral helper tests are NOT WPF/installer runtime verification. Never label a stable public release tested without the actual native gate.
- Use separate Git worktrees for independent writers. Do not edit a sibling's files or integrate while they are still writing. Independently review final frozen bytes, then rerun gates in the integrated tree.
- Only the connected ChatGPT Plus provider is authorized for LLM inference. Never use Copilot or API-key inference providers.
- No automatic stable release, force push, main/develop merge, vault migration, service restart or signing-certificate purchase. Save passing work on a recovery branch and verify its remote commit; expose blockers plainly.

## Continuity

The authoritative remote development baseline is the validated branch above. A previously documented rescue machine used `/root/projects/LaunchFlow-rescue` and `/root/projects/launchflow-evidence`; those are historical paths, **not** assumed present in current tooling. Current architecture, inventory and acceptance ownership are tracked in `architecture/` and `stabilization/`; all CI claims must be tied to an exact commit/tree and GitHub run.

Other project automation is intentionally paused by the user's exclusive-focus instruction. Do not resume it until the user changes that instruction. Keep existing servers/services running. Project focus state and paused IDs are recorded outside the public repo at `/root/projects/launchflow-evidence/focus-state.json`.
