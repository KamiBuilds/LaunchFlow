# Workspace / monitor / localization regression tests

```bash
DOTNET=/root/.cache/launchflow/dotnet/dotnet bash tests/workspaces/run.sh
```

`WorkspaceTests.csproj` compiles the actual production models, MonitorService and
LocalizationService, without NuGet test-framework dependencies. It exercises old
JSON defaults, round-trip retention (including unknown config/workflow/workspace
fields), named capture/update, disabled/deleted/duplicate workflow IDs, monitor
identity/reordering/disconnection and locale selection. `PlatformStubs.cs` replaces
only Windows `Screen` enumeration; selection logic is production code. It does not
prove Windows device naming or native browser window placement.

`extract-ui-methods.py` copies unchanged current MainWindow method declarations and
bodies into ignored `obj/UiMethods.g.cs`. The test executable compiles those bytes
against explicit control, master-password, vault, engine and recorder-dialog seams
in `UiHarness.cs`. Run the extractor again after every production change; the run
script does so. The legacy lock adapter exists only to reproduce the synchronous
baseline failure; it is not app code.

The UI suite exercises immediate key revocation before delayed browser cleanup,
nonblocking/coalesced cleanup, cancelled engine completion before session teardown,
run-button restoration and modal reentrancy, workspace resolution, failure versus
success notifications, no-selection safety, unlock revocation across a modal dialog,
and the original workflow identity across modeless Teach plus authorization loss.
All credentials, IDs, screens and dialogs in these seams are synthetic.

These are executable method-level seams, **not WPF runtime, native DPAPI, browser
engine or Windows installer assurance**. The full Windows application must also
cross-build, and native Windows gates in `docs/BOTH-UI-INTEGRATION.md` remain open.
