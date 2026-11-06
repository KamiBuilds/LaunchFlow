# LaunchFlow v0.7.46 — controlled native acceptance record

**Frozen reviewed baseline:** `cbbd52326bf725538df56781946838f7f92a44c0` (GitHub Actions 41993466370). Any later test must identify its **own** exact Git SHA, build artifact and hashes. Do not transfer results across commits. The original artifact 13399323599 contained the verified installers:

| Artifact | Reviewed SHA-256 |
|---|---|
| LaunchFlow-Setup-v0.7.46-x64.exe | `64b85ed5683d8ae1a5eef2ca1d3d1fbeb26b20e2c5b3ac77a8a75e3b436f1ef7` |
| LaunchFlow-Universal-Installer.exe | `ab6aa80f3d56301b4f18bc2f0402788bf196f61e87552994fb10debf6202c420` |

Those hashes identify the **reviewed baseline**, not later installers. Exact changed-artifact hashes must be read from that new run's SHA256SUMS.txt.

## Native Windows installed checks

The Windows GitHub-hosted build runner can execute a limited actual Windows silent-installer smoke test; it is not equivalent to an interactive Windows desktop/user acceptance environment. `tests/native/InstallerSmoke.Tests.ps1` refuses an occupied application or data directory. It installs the exact built setup silently as the current per-user account, runs the executable's non-vault shutdown entrypoint, exercises same-version repair and silent uninstall, and compares synthetic retention-marker hashes. It does **not** run normal WPF dialogs, upgrade from a distinct older version, or decrypt any real vault.

| ID | Acceptance journey | Status | Expected proof / blocker |
|---|---|---|---|
| N1 | Fresh actual UI installation, first vault with two distinct credentials, close/reopen | **NOT RUN** | Interactive Windows user and independently recorded installed binary, persistent values and workflow IDs |
| N2 | Older supported installation upgraded with retained encrypted data | **NOT RUN** | Disposable older installer/data and user-session upgrade verification |
| N3 | Username-only, password-only, preference edit and rejected persistence through WPF | **NOT RUN** | Actual Windows editor and fault injection with cold persisted state |
| N4 | Incorrect/successful master-key rotation, injected failure and restart | **NOT RUN** | Actual WPF prompt and encrypted SQLite cold reopen |
| N5 | Import modal lock/unlock, continuous lock and normal import in WPF | **NOT RUN** | Actual WPF modal/dispatcher test with isolated synthetic backup |
| N6 | Password-inclusive/config-only/empty export, prompt lock, failed replace, separate restore | **NOT RUN** | Windows file-sharing and recovery tests |
| N7 | Tray/ordinary Exit and lock during run/Teach with profile seal failure | **NOT RUN** | Actual browser/WPF/DPAPI/profile lifecycle |
| N8 | Silent per-user install, same-version repair, data-preserving uninstall on Windows CI | **PENDING exact-head CI** | Hosted Windows native silent smoke; not interactive acceptance |
| N9 | Windows user-scope startup, actual upgrade, optional browser consent, uninstall dialog | **NOT RUN** | Disposable interactive Windows user machine |

For every interactive case, record `PASS`, `FAIL`, `BLOCKED`, or `NOT RUN`, exact OS/account scope, installed EXE SHA-256, fixture IDs, expected and observed states, and redacted evidence. A screenshot or script written but not executed is not execution evidence.

## Release blockers and owner decisions

- **S3 credential ownership — strict v3:** The original **cross-workflow row-substitution** counterexample was repaired in the strict v3 authenticated workflow/purpose/version/vault binding and regression-tested in the prior verified source. Rerun the S3 suite on each final refactor SHA; neither this refactor nor those tests remove the known **same-owner ciphertext replay** possibility, untrusted complete-rollback freshness, or historical legacy-association uncertainty. No new migration or rollback guarantee is claimed here.
- **Incomplete vault artifact recovery:** Startup must distinguish absent storage, structurally complete storage needing authentication, and incomplete/unsupported storage **without deleting or rewriting** files. Structural inspection does not validate decryption. Any quarantine/reset requires explicit consent and a recovery copy. The read-only classifier and its synthetic tests are not proof of Windows sharing/permissions.
- **Native interactive acceptance:** Still **BLOCKED / NOT RUN** until an authorized interactive Windows test account/machine executes N1–N7 and N9 with disposable fixtures.
- **Release publication:** No protected merge, signing procurement or public release authorized. Green CI and matching checksums prove only their own executed contracts.

## Execution protocol for disposable interactive acceptance

Use a fresh disposable Windows user and copy the exact tested installer artifact locally. Verify SHA256SUMS.txt before running setup. Record `winver`, user scope (not personally identifying account information), install path, installed LaunchFlow.exe SHA-256 and data directory. Never substitute a different installation merely because its version string matches.

Keep three synthetic workflows/accounts: A and B with distinct values, and an import-only C. Use a separate encrypted backup and a separate disposable installation for restore. For each modal scenario, use a controlled lock event and compare both live values and a fresh reopened encrypted vault. Preserve any unexpectedly altered test vault before retrying. Never enter real passwords or access unrelated networks.

Native checks must be reported as not run unless their exact observed results are attached to the build under test.

## Additional strict v3 Windows acceptance — NOT RUN

The separate security-format upgrade requires explicit interactive acceptance before beta. On a disposable Windows account, install a supported previous v1/v2 version, create synthetic workflows with distinct passwords, retain a WAL-active encrypted fixture and verify old-version cold access. Exercise upgrade cancellation, wrong password, failed backup and successful consent through actual WPF dialogs. Verify retained original plus recovery copy, disabled automatic execution, exact cold v3 credential mapping, old-client refusal and old-reader use of a separately restored recovery snapshot. Exercise lock/exit during prompts, backup/uninstall permissions and an attempted warmed older writer. Preserve and compare the exact selected installer SHA-256. These tests are **NOT RUN** until actually executed and recorded on the final source artifact.

## 2026-11-06 structural implementation candidate

The implementation branch `refactor/core-structure-20261106` starts from `93b49482b6ea197c810e27def9fe6d9b080ec0aa`. UI run, editor and website mapping ownership plus login staging were refactored without a version, AppId, vault format or profile-format bump. The first branch CI `42226865019` did **not** reach Windows because workspace regression compilation failed; corrected at `a1fe9967c9b9dd9163870b680180e72f6689ece8` pending the newer full gates. Older installer SHA256 values above cannot identify this candidate. N1–N7 and N9 remain **NOT RUN** on an actual owner Windows desktop; hosted Windows build/silent smoke is distinct from owner acceptance. Owner A/B/C data must be synthetic.
