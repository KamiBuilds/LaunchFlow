# LaunchFlow end-to-end acceptance matrix (M0)

**Source baseline:** `941c5f13bd993ffa18e0d883e77448379f7b54a5` (tree `3af53c0b048f0eac5f471df8cfa045bd687f3cf4`). The source is a successor to the document's reviewed `34befa061...` baseline; earlier RED results are historical evidence, not outcomes from this tree.

**Latest exact-source CI:** [run 41687485233](https://github.com/KamiBuilds/LaunchFlow/actions/runs/41687485233) — Linux 20 steps SUCCESS, Windows build/installer tests SUCCESS, third job verifies unsigned installer artifact names and SHA-256 hashes SUCCESS. The independently reviewed six failure scenarios were previously reproduced RED at `34befa0...` and turned GREEN in synthetic CI on the repaired source. PR-independent review of this final tree is **not** supplied. Do not equate CI green with native or vendor acceptance.

**Artifact from that run:** `LaunchFlow-v0.7.46-941c5f13bd993ffa18e0d883e77448379f7b54a5-Windows`, GitHub artifact ID `13256691698`; unsigned development installers, not signed release binaries. Existing app/product/data identity remains v0.7.46.

## Case-by-case status

Statuses are intentionally scoped: **Passed — synthetic** is not a native/vender pass; **Partial** means some lower-level evidence exists but this end-to-end family is not accepted; **Open** is a live concern requiring RED/GREEN; **Blocked** requires authorized native/external testing. No family is declared fully end-to-end accepted solely from CI.

| ID | User journey / negative requirement | Current status and evidence level | Observed evidence / missing scope |
|---|---|---|---|
| AC01 | Clean install, launch, setup/unlock on a clean Windows PC | Blocked — native acceptance | CI builds two unsigned self-contained installers; actual first launch/setup not exercised |
| AC02 | Old encrypted data + upgrade preserve IDs/preferences/credentials | Partial — automated compatibility | CI baseline-to-new-to-baseline encrypted compatibility; no real in-place Windows upgrade |
| AC03 | Create/edit and cold-reopen website/workspace | Partial — production-linked | Autosave/workspace tests; no complete installed-path cold reopen |
| AC04 | Edit during autosave/selection/same-ID import | Partial — regression subsets | Production WPF autosave test and save transaction coverage; combined overlaps unverified |
| AC05 | Storage precommit rejection vs committed/not-published | Partial — component | ConfigurationSaveTransaction result distinctions; UI/native status after write failure unverified |
| AC06 | Email-only/password-only/no-op/removal/selection guard | Partial — source/selected checks | Blank-field preservation source present; complete live-vault matrix not demonstrated |
| AC07 | Atomic credential + preferences failure and cold reopen | Open — source concern A3 | Separate Vault.Save then ConfigStore.SaveDurable in MainWindow.Credentials.cs |
| AC08 | Homepage entry and owned combined-form submit | Passed — synthetic Chromium scope | Full runtime CI, AutomaticEntryTests + original independent reviewer probes |
| AC09 | Identifier and password stage Log In/Sign in/Next/Continue | Passed — synthetic Chromium scope | Full runtime CI, ReliableStageLoginTests and StagedLoginTests; no vendor claim |
| AC10 | SPA, delayed controls, resumed password | Passed — synthetic Chromium scope | Full runtime CI; synthetic same-URL/delayed/resumed fixtures |
| AC11 | Explicit custom button and intentional no-submit | Partial — Chromium | F5 configured resumed action positive; other no-submit/ambiguous combinations pending |
| AC12 | Newsletter/signup/reset/missing-password/ambiguity | Partial — Chromium | Independent F3, earlier wrong-form/newsletter controls pass; full ambiguity matrix pending |
| AC13 | IdP approved/unmapped, mapped/denied and frames | Passed — tested synthetic variants | F2 + F4 and 32 document-bound cases (108 assertions), synthetic only |
| AC14 | Verification wall despite mounted account chrome | Passed — synthetic reproduction | F1 plain/semantic challenge with counterfeit account controls |
| AC15 | SSO with proof, downstream action and conflicting explicit postcondition | Partial — Chromium | F6 SSO positive; contradictory custom postcondition variants pending |
| AC16 | Real OTP/push/passkey/CAPTCHA interaction + observe/rejection | Partial — negative controls | Challenges blocked; actual authorized human completion/observation untested |
| AC17 | Cancel during readiness/approval/fill/submit/completion | Partial — browser regressions | Independent F4 approval cancellation and earlier L3 origin-policy revocation; full phases pending |
| AC18 | Element/form/frame/document replacement + policy await | Partial — production-linked | DocumentBoundCredentialTests 32/32, PolicyDispatchTests; all combinations pending |
| AC19 | Prompt/import changes selected run scope and revision | Partial — tests/source | WorkspaceCatalog scope tests and guarded UI run path; complete same-ID concurrent import entry path pending |
| AC20 | Step failure/redirect/cancellation blocks dependent work | Partial — browser regressions | Runtime step policy/redirect tests; full supported-step variant matrix pending |
| AC21 | Repeated run/session reuse without secret read/duplicate effects | Partial — browser fixtures | Synthetic already-authenticated session no vault read; native multi-monitor/browser reuse pending |
| AC22 | Teach close/drain/late callbacks/same-ID publication | Partial — production-linked | Recorder/workspace integration coverage; native modeless lifecycle unverified |
| AC23 | Retained modeless Repair while newer run active | Open — source concern A2 | RepairLoginCoreAsync lacks current-run admission; RED test required |
| AC24 | Launch/close/unclaimed context/failed close/repeated cleanup | Partial — policy/lifecycle | EngineLifecycleTests and policy seams pass; ordinary WPF exit remains open |
| AC25 | Partial profile cleanup and recoverable reopen | Partial — simulated/automated | Profile cleanup and encrypted recovery CI gates; native process/profile acceptance pending |
| AC26 | Windows lock/unlock grace + stale/unlock/Exit races | Partial — simulated | Session lock policy and WPF seams; native event interleavings pending |
| AC27 | Ordinary/tray/installer Exit await cleanup, failure retry | Open — source concern A1 | Installer awaited path exists, ordinary OnClosed async void is later than close |
| AC28 | Transfer validation, size/replace prompts and recovery | Partial — existing checks | Import/transfer source and policy CI; full malformed/oversized/cold restore unverified |
| AC29 | Master password/migration/backups/reset preserve data | Partial — compatibility | Legacy compatibility and vault tests; full native failure/reset controls unverified |
| AC30 | Browser setup/consent/missing browser and reused sessions | Partial — synthetic/platform seams | Browser choice/install and engine tests; native browser setup/denial pending |
| AC31 | Update staging, signatures, race/cancel/recovery | Partial — build-only | Installer behavior and release-readiness SHA-256 verification; native updater behavior untested |
| AC32 | Startup registration vs durable setting failure | Partial — source/platform seams | Startup service tests; saved-but-registration-failed UI/native path unverified |
| AC33 | Keyboard/focus/DPI/localization/tutorial guide | Partial — static and integration | UI + guide source checks pass; native visual and accessibility tests pending |
| AC34 | Diagnostic canary redaction and bounded retention | Partial — automated tests | Diagnostics privacy tests; complete nested/error canary matrix pending |
| AC35 | Repeated operations, resource/soak, performance budgets | Unexecuted | No supported multi-day resource soak or measured budget |
| AC36 | Installed native Windows + authorized vendor/region/account | Blocked — external acceptance | No native user-run ThousandEyes or other production app account acceptance |

## Required evidence per case before promoting status

Record `requirement-id`, `source-SHA/tree`, `test input + entrypoint`, `command and run ID`, `test owner`, `expected vs observed outcome`, `platform`, `artifact checksum`, and `native/vendor limitations`. Record separate RED, isolated rerun, hosted full gate and final GREEN; no swapping outcomes from different SHAs.

## Critical first repairs after M0

- **A1 / AC27:** add executable ordinary/tray Exit tests with delayed browser close and failed cleanup, then change production to await the same owned shutdown barrier used by the installer; keep dirty-locked denial.
- **A2 / AC23:** real modeless dialog A, new run B, late Repair choice; assert zero recorder acquisition and zero stale recorded-step publication.
- **A3 / AC07:** real encrypted SQLite transaction/rejection test for credential/preferences dual save, cold reopen actual committed state. Prefer one narrow transaction or explicit saved-partially outcome; no fragile rollback of a committed secret.

## Site and platform verification matrix

| Platform / site | Browser/account method | Status |
|---|---|---|
| Linux CI | Isolated Playwright Chromium, fully intercepted synthetic HTTPS | Production-linked runtime PASS; no desktop WPF validation |
| Windows CI | Builder image, Inno Setup, self-contained distribution + installer unit tests | Build PASS; no GUI install/launch/upgrade acceptance |
| Windows end-user machine | Edge / Chrome / Firefox, 1–6 monitors, startup/unlock and tray | Unexecuted as a systematic native acceptance matrix |
| ThousandEyes | `app.thousandeyes.com/login`, actual tenant/region, two-stage sign-in | Synthetic ThousandEyes-shaped case PASS; real logged-in password page and post-login workspace **not certified** |
| Nginx Proxy Manager | Owned proxy dashboard instance | Site-specific real acceptance not recorded |
| npmjs.com | npm package registry account | Not interchangeable with Nginx Proxy Manager; no site-specific certification |
| SSO/IdP | User-approved redirect, explicitly mapped credential destination | Synthetic authorization tests PASS; actual provider/session acceptance requires consent |
| MFA/push/passkey | Human completes provider challenge | Automated bypass prohibited; native observation and safe resume not verified |

## Release decision

**Not stable / not ready to publish.** Source and test fixture improvements alone do not constitute independent review, cold upgrade evidence, native usability or vendor authentication. Release remains separately gated by explicit user approval, signature validation and platform evidence.
