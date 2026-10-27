# LaunchFlow rescue contract

LaunchFlow is the user's active engineering priority. The current stabilization pass works only on `hermes-next`, based on `both-3` at `25df42a7e2ce844b47a716891e214283ff4c1983` (v0.7.20). Preserve `both`, `both-2`, `both-3` and collaborator branches. Follow `docs/HERMES-NEXT-STABILIZATION.md`; v0.7.21 is a proposed future release, not permission to bump versions or publish. Earlier rescue work from `feature/kiss-rule-sol` (`5645305`) remains isolated in sibling worktrees and may be integrated only after compatibility review. Preserve original product identity and licensing; do not invent a license or new product.

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

Main checkout: `/root/projects/LaunchFlow-rescue`. Pre-change backup: `/root/projects/LaunchFlow-before-rescue.bundle`. Disposable development .NET SDK: `/root/.cache/launchflow/dotnet/dotnet`. Small RED/GREEN/build evidence: `/root/projects/launchflow-evidence`.

Other project automation is intentionally paused by the user's exclusive-focus instruction. Do not resume it until the user changes that instruction. Keep existing servers/services running. Project focus state and paused IDs are recorded outside the public repo at `/root/projects/launchflow-evidence/focus-state.json`.
