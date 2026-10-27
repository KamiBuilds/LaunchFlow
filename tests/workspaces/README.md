# Workspace / monitor / localization / UI integration regressions

```bash
DOTNET=/root/.cache/launchflow/dotnet/dotnet bash tests/workspaces/run.sh
```

`WorkspaceTests.csproj` links production models, MonitorService and LocalizationService.
Eight tests exercise old JSON defaults, round-trip retention including unknown fields,
workspace identities and resolution, monitor identity/reordering/disconnection and
explicit/automatic locale selection. `PlatformStubs.cs` replaces only Windows Screen
enumeration. This does not prove native display naming or browser placement.

`UiTests.csproj` links the **actual EnterprisePolicy and CredentialVault services**.
The policy test provider is the production AsyncLocal seam; the suite does not
reimplement policy parsing, flags or URL rules. VaultDatabase is a synthetic in-memory
storage/key seam, not the native encrypted database. No real credentials are used.

`extract-ui-methods.py` copies unchanged current MainWindow method declarations and
bodies, plus the two App crash callback bodies, into ignored `obj/UiMethods.g.cs`.
Missing methods/signature drift fail extraction rather than silently skipping tests.
The run script regenerates before every UI run. Controls, dispatcher scheduling,
password/KeePass dialogs, recorder completion, LoginHealth, diagnostic sinks, profile
sealing, KeePass CLI and browser engine ownership are explicit portable seams.
Even the KeePass run-prompt decision uses the actual extracted production method.
Async-void event handlers run under a tracking SynchronizationContext so their
post-await policy checks and completion are awaited rather than guessed with sleeps.
Temporary KeePass paths are empty synthetic files; no KeePass process is launched.

The UI suite covers policy denial and fresh post-modal/post-await checks, no
credential-loss/fallback on denied reads, SSO no-secret behavior and exact mode
mapping, mixed final failure/login/step attention, repair IDs with duplicate names,
queued/stale status callbacks, modeless Teach identity and authorization, immediate
key revocation before synchronous cancellation callbacks, reentrant/coalesced locks,
close-before-observe for stalled run/Teach dispatch, failed-close no-seal/admission
retention/retry, and fatal versus dispatcher recovery. The former test requiring
run completion before close was deliberately reversed: the combined engine's close
barrier must terminate outstanding browser dispatch first. Historical evidence is
retained; this is not a relaxed assertion that cleanup ordering is unimportant.

Both projects have distinct `obj/<project>/` and `bin/<project>/` directories via
`Directory.Build.props`, plus explicit compile items. Previously shared artifacts
could make `dotnet run` execute the sibling harness's stale executable; verify that
output includes both the workspace and UI summaries.

These are **executable orchestration seams**, not actual WPF event delivery,
Windows Registry/DPAPI, browser dispatch cancellation or installer assurance.
`docs/BOTH-UI-INTEGRATION.md` records exact parent contracts and native gates.
