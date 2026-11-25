# Native installed-vault acceptance

These tests exercise the **installed Windows application**, not a replacement
storage implementation. Run only under a fresh disposable Windows user or VM:
the harness refuses an existing LaunchFlow data directory, installation or app
process and must not be pointed at a live user's vault.

The dedicated `Vault reliability native acceptance` workflow is restricted to
the repair branch. It uses read-only repository permissions and no credential,
signing or release resources. It first tests the actual Windows PowerShell 5.1
nested automation host and input mechanisms. Only then does it build an exact
candidate archive outside Git, run the original CMD builder, install that same
installer and verify the whole installed runtime payload.

Installed scenarios:

- Keyboard, actual non-ASCII and explicit clipboard setup/main/full exit/cold
  owner unlock/main/final full exit. Input modes never fall back to one another.
- Short-input refusal, editable correction and the complete healthy lifecycle.
- Wrong-password refusal followed by the selected owner's successful unlock.
- Explicit incomplete-database recovery cancellation with the complete synthetic
  data-root file/directory set and byte hashes preserved. Only the two fixed
  root-level `startup-diagnostics.log` / `.previous` files may change (regular
  files, each at most 256 KiB); main-file and absent WAL/SHM checks also apply.

App spawn intent is persisted before launch and retired only after verified
PID/start/path/session ledger publication. Interrupted registration is reported
as unknown (`remaining=-1`), never an empty inventory. Product cleanup remains
graceful and restricted to owned identities/handles.

The original CMD build joins a private kill-on-close Windows Job Object at process
creation through `PROC_THREAD_ATTRIBUTE_JOB_LIST`, alongside the explicit standard
IO `HANDLE_LIST`. The job handle is not inherited. The suspended child's actual
membership is verified before resume; unsupported hosts fail closed with no
post-creation assignment fallback. The fixed 40-minute deadline includes descendants; failures
terminate only that build scope, then observe it for a bounded cleanup period.
`build-result.json` records fixed category/reason, exit code and residual count
(`-1` if unverified), separately from successful package provenance.

Values are generated in the worker and compared only in memory. Password fields
are not read through UI Automation. No credentials in environment/arguments,
clipboard dumps, UI trees, screenshots, raw streams or vault files are uploaded.
Public results are constructed from strict fixed schemas and required milestone
sets; acceptance requires every applicable milestone and zero owned processes.

The installer package and safe result artifacts have short retention. Deleting
visible resources cannot establish erasure of provider caches or copied code.
The installer is unsigned unless independently authorized signing is configured;
this test workflow does not publish releases.

## Portable preparation

```sh
python3 tests/native-vault/verify_portable.py \
  --pwsh /path/to/pwsh --dotnet /path/to/dotnet \
  --evidence /path/to/owned/results
```

This checks PowerShell syntax, strict schemas, manifests/provenance, JSON rewrite,
process identities, real portable child registration-fault/watchdog accounting,
the actual recovery scenario's modeled-UI filesystem oracle, portable input
planning and C#5 Windows reference compilation. The original ten preparation
checks remain, with `Test-JsonShape.ps1` added as an eleventh check. It exercises
empty/single/multiple JSON root arrays, scalar provenance and strict rejection
of nested arrays, null roots/entries and malformed records. The actual-host
controls are distinct from the explicitly modeled nonenumerating conversion
boundary. `Read-BoundedJson` emits exactly one array level on supported hosts;
invalid null roots cannot masquerade as an empty process ledger.
**It does not execute WPF, Windows PowerShell 5.1, Win32 input, Job Objects or the
installer.** The build-owner portable gate reports its native portion as skipped.

## Windows entry points

Credential-free actual host/input preflight:

```powershell
& ./tests/native-vault/Test-NativeVault.ps1 -Case preflight `
  -ResultRoot "$env:TEMP/vault-preflight-unique"
```

The workflow also requires `Test-BuildOwnership.ps1` in actual Windows PowerShell
5.1. It runs `Test-BuildJob.ps1`: healthy child/grandchild completion, short tree
timeout, exception, root-exits-before-descendants, suspended-creation failure and
abrupt-owner kill-on-close controls. A separate control forcibly kills the owner
immediately after successful suspended creation, before membership verification
or resume, not merely after descendant readiness. Its private readiness receipt
transfers an exact non-inheritable process handle to the observer (never the job),
with PID/creation-time/path/session validation and bounded fixture-only cleanup;
the unrelated synthetic sentinel must survive. These are separate from the
40-minute original build. The portable missing-JOB_LIST RED/GREEN regression is
source-level evidence, not a Windows orphan reproduction; compilation cannot
satisfy the mandatory Windows controls or prove arbitrary interrupted-kernel-call behavior.

Installed cases require the exact package produced by `Build-Candidate.ps1` and
its selected full source SHA; see the workflow for the complete build/package
and fresh-recipient invocation. Do not substitute a release/latest download,
old installer, baseline manifest, fake summary or a patched DLL from another
build. Case-specific reports distinguish harness, application and timeout errors.
Missing native execution remains an unverified platform gate, never a pass.

Hosted Windows is not the unavailable affected VMware environment. Passing these
checks alone does not prove the reported valid-password incident is resolved.

## 25 November 2026: Unicode and wrong-password native fixes

The installed-product `valid_unicode` case keeps real SendInput Unicode
entry (including non-Latin and combining code units). The input helper now
dispatches paced UTF-16 key pairs instead of one large input burst.
No clipboard, accessibility-password readback or fallback is substituted.

The `owned_wrong` case asserts the application rejects the wrong password
and then binds to the newly created unlock dialog. The old dialog correctly
closed after handing the wrong password to the application; matching its old
window handle was an invalid harness assumption.

EnumWindows includes non-modal WPF tooltip/popup HWNDs. The harness checks
UI Automation control type to ignore non-window surfaces, and while waiting
for Main it uses the repeated visible/enabled `MainTabs` readiness gate
rather than treating incidental popup surfaces as modal errors. Recognized
startup failures and unexpected password/confirmation dialogs still fail.

Both cases passed the focused Windows replay on exact source-linked harness
commit `15e4d656b2d59456d84f28751d82a98bbfc50595` against the previously
built application payload `cc1135cdf5046a2b24237fbcdc4f579155af0eeb`
(GitHub Actions run 45595780977). This proves those native harness changes
against those application binaries, **not** a final exact-current-source
installer build or an independently verified affected VMware machine.
The one-time replay workflow was removed to avoid depending on an expiring
artifact. The permanent vault-reliability acceptance workflow must pass
on the final integrated commit before release.
