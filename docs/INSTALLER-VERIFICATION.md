# Installer verification and release policy

## Release boundary

- Pushes to `hermes` and `chatgpt`, pull requests, and manual dispatch run the Windows build and installer tests. Ordinary builds have read-only repository permissions and upload CI artifacts only.
- `publish_release` defaults to false. A manual draft request additionally requires the exact `DRAFT-v<VERSION>@<full commit SHA>` confirmation and a nonblank native-Windows evidence reference. This is a publisher attestation, not automatic validation of that evidence.
- The separate draft job uses the `windows-release` environment. Repository administrators must configure its required reviewers before release use; naming an environment alone does not enforce review.
- Draft creation refuses an existing release or an inconclusive lookup. It uses create-only REST POSTs, not release upserts, and reads back the exact new release ID and uploaded asset names, sizes, states and SHA-256 digests. A failure can leave a partial draft for manual inspection. It never publishes stable or marks a release latest.
- Stable publication remains a separate human decision after testing the exact artifacts. No release or repository remote write was performed during this local rescue.

## Automated coverage

Run on a developer machine with .NET 8 SDK and PowerShell:

```powershell
dotnet run --project ./tests/installer/Installer.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Installer behavior tests failed' }
dotnet run --project ./tests/bootstrapper-form/FormTests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Bootstrapper form behavior tests failed' }
./tests/installer/Packaging.Tests.ps1
if ($LASTEXITCODE -ne 0) { throw 'Packaging behavior tests failed' }
./tests/installer/Parse.Tests.ps1
```

The C# console harness links the production release policy, HTTP and verified-download files. It tests exact stable tag/asset selection, malformed and duplicate metadata, repository/tag/filename-bound HTTPS URLs, required GitHub SHA-256 digest and size, redirect allowlisting and limits, bounded JSON, cancellation, truncation, overflow, wrong digest, disconnected input, output errors and HTTP failures. Its metadata and tiny byte arrays are explicitly synthetic, not live release evidence.

PowerShell checks deterministic artifact selection, release authorization, missing runtime files and malformed runtime declarations. Builder tests execute the real builder in a disposable fixture with inert SDK/compiler/signing doubles, checking successful output and missing, empty, compiler-failed and publish-failed output without choosing unrelated stale artifacts. Workflow tests execute the shipped draft script against a REST double, including unsafe creation, upload failure, bad digest and public-release read-back. These are offline control-flow checks, not native installer runs or live GitHub publishing tests. Parse checks use the real PowerShell parser on scripts and embedded workflow commands.

Payload checks require bundled .NET 8 core and Windows Desktop runtimes, managed/native SQLite, and Playwright's Windows x64 Node, current bundled driver files and licenses. The Playwright 1.63 layout uses `coreBundle.js`, `utilsBundle.js` and `serverRegistry.js`; the obsolete `lib/server/registry/index.js` path must not be used as a packaging requirement. Revalidate this list against the actual NuGet publication when changing Playwright. The Inno file list explicitly includes the private `.playwright` tree.

Neither installer layer installs a browser or accepts browser/vendor terms. A missing supported browser is an **app-managed setup state**, not a reason to install an SDK, bypass consent or claim installation/automation succeeded. The parent app changes must provide that setup flow.

## Local rescue evidence

Evidence is retained outside Git at `/root/projects/launchflow-evidence/installer/`:

| Evidence | Observed result |
| --- | --- |
| `01`–`20` RED/GREEN logs | Recovered original failures and subsequent checks; retained without fabrication |
| `23`–`25` recovered-suite logs | Original on-disk suites passed before further changes |
| `26`/`27` | Missing managed SQLite regression: RED, then GREEN |
| `28`/`29` | Duplicate release/asset fields: RED, then GREEN |
| `30`, `35` | Additional builder/workflow characterization checks passed first run; not claimed as TDD RED |
| `31`–`33` | Invalid runtime version RED; first fix regressed valid versions due to PowerShell's automatic `$Matches`; corrected GREEN. The failed `32-...green.log` filename is historical, not a passing verdict |
| `38-final-gates.log` | Real prior payload rejected the stale Playwright registry path after unit checks passed; retained as a genuine integration failure |
| `42-final-gates-green.log` | Payload-layout correction passed against the actual prior publication |
| `44-repository-literal-red.log` | Explicit malformed repository inputs were silently trimmed before validation; negative tests caught both cases |
| `45-final-gates-green.log` | Final CI test sequence: C# **69/69**, packaging/workflow **62/62**, parser **7 files + 8 workflow scripts**, zero failures; actual prior payload assertions then passed |
| `34-bootstrapper-publish.log`, `40-final-bootstrapper-build.log` | Windows x64 self-contained bootstrapper cross-published; Release build **0 warnings, 0 errors** |
| `39-prior-driver-version.log` | Captured driver CLI reports **1.63.0** under the developer Linux Node; no browser installed or launched |
| `41-isolated-version-gate.log` | Builder correctly refused this isolated worktree's old app `0.7.11` versus integration-target installer `0.7.19` |
| `43-audit.json` | Machine-checked totals, workflow YAML policy, unchanged AppId/app sources, archive integrity, artifact SHA-256 and log hashes |

The actual interpreter was existing developer-cache PowerShell **7.4.21**; its retained official archive matches captured release metadata. SDK **8.0.427** is under `/root/.cache/launchflow/dotnet` (executable `dotnet/dotnet`, not `dotnet8.0.427/dotnet`). No system package or browser installation was performed.

The bootstrapper artifact is `final-bootstrapper-win-x64/LaunchFlow.UniversalInstaller.exe`, **170654129 bytes**, SHA-256:

```text
a84c2785e19dbf1dc81e2046b728a70f22d938c05c70dd7845852303abd3acb3
```

It was published with `-r win-x64 --self-contained true -p:EnableWindowsTargeting=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false`. PE inspection confirms AMD64; it was **not executed on Windows**. Prior app payload evidence is from the isolated `0.7.11` app, **not final `0.7.19` acceptance**. Installer `AppVersion` is intentionally `0.7.19` for integration with the parent branch; app project/version files were not changed. AppId remains `{9B1A7C15-093B-4DBA-BDA7-BF43A2E67320}`.

Independent review was unavailable after the earlier reviewer quota failure. Local verification continued as requested; there is no independent approval claim.

## Parent integration checkpoint

The parent reconciled this work with the actual `chatgpt` v0.7.19 source in an isolated integration worktree. Real PowerShell and .NET checks again passed: C# **69/69**, packaging/workflow **62/62**, parser **7 files + 8 workflow scripts**, and bootstrapper cross-build **0 warnings, 0 errors**. A newly published self-contained v0.7.19 app payload passed the shipped runtime/driver/SQLite assertions and reported assembly version `0.7.19.0`; it was not run on Windows.

Integration caught a newer-branch regression: merely building artifacts could close and ultimately force-kill the user's installed LaunchFlow process. An inert process-discovery/termination guard reproduced five builder control-flow failures, then passed after removing that behavior. The builder now leaves running applications alone. Application closure belongs to the separately launched Windows Setup. Historical RED and subsequent GREEN are retained as `parent-build-preserves-running-app-RED.log` / `-GREEN.log`.

Independent review withheld approval on a reproduced cached downgrade decision: a concurrent upgrade could finish after refresh while this form still authorized the older release. The repair re-reads installed-version policy at download entry and again after verification before setup launch. Maintained tests in `tests/bootstrapper-form` link the actual form/helpers with inert UI, registry, HTTP and process seams. They reproduce both stale-entry and during-download failures, then pass after the respective guards. Positive controls cover fresh blocking, same-version reinstall, child/staging lifetime, close deferral and download cancellation. Their three-byte fixture is checked, never executed. The initial unused-fixture-field compilation failure is retained separately from the runtime RED evidence.

These are application-policy checkpoints, not an atomic OS-wide installation transaction or protection from same-account/admin replacement. Native installer coordination and final integrated review remain required; simulated child lifetime is not native Inno proof.

## Remaining native Windows gates

Before creating a release candidate, integrate with the parent `0.7.19` app and obtain a successful Windows CI run including real Inno compilation and final payload assertions. Then test the exact checksummed artifacts on clean Windows 10/11 x64 without a developer SDK or separately installed .NET runtime:

1. Install per-user, launch, create/unlock the vault, and exercise real SQLite persistence and the bundled Playwright driver.
2. Upgrade and repair in place with the unchanged AppId; verify workflows, credentials, backups and existing data are preserved. Test app/tray process closure and locks.
3. With supported browser present, exercise automation. Without one, verify explicit app-managed browser setup, consent, refusal, network failure, cancellation and retry; no silent install or accepted terms.
4. Exercise offline, rate-limited, malformed-metadata and interrupted downloads, cancellation/window close, digest failure, temp cleanup, setup cancellation/nonzero exit and recheck/reinstall/downgrade behavior. Verify actual Inno child-process lifetime and file locks.
5. Test uninstall keep-data, explicit wipe and cancel independently. Verify data preservation is the safe default and no user state is removed by downloader failure.
6. Inspect Authenticode/SmartScreen behavior on the actual distributed EXEs and uninstaller. Unsigned local artifacts are not signing approval.
7. Configure release-environment reviewers, verify draft-only behavior with the real API when separately authorized, and verify final release asset digests before a human publishes stable. No live release API write has been exercised locally.
