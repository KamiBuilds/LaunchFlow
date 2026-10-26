# Credential destination regressions

The runtime harness links the production engine and Microsoft.Playwright **1.63.0**. Only vault/OS seams are stubbed. Browser contexts intercept every request and serve synthetic HTML; no real sign-in or credential database is used. Input tracking records **field IDs only**, including transient fills before a later document replacement clears a field.

Run from the repository root with the .NET 8 SDK and the matching development Chromium installed:

```sh
dotnet run --project tests/runtime/RuntimeTests.csproj -- --document-bound-only
dotnet run --project tests/runtime/RuntimeTests.csproj -- --document-bound-only --case=delayed-password
dotnet run --project tests/runtime/RuntimeTests.csproj
dotnet run --project tests/profiles/ProfileTests.csproj
dotnet build LaunchFlow/LaunchFlow.csproj -p:EnableWindowsTargeting=true -p:PublishReadyToRun=false -warnaserror
```

Set `PLAYWRIGHT_BROWSERS_PATH` when Chromium lives outside Playwright's default cache. An unknown `--case` fails rather than silently running nothing. The original runtime regressions remain in `Program.cs` and still run by default.

## Threat reproduced

`delayed-password` preserves the independent reviewer's disabled-password fixture: the username continuation schedules cross-origin navigation after 1200 ms, later than the engine's existing 800 ms continuation delay. The old live locator waits for editability and resolves again in the replacement document, without another approval. The regression requires that document to stay empty, not that the leak occur.

Both username and password additionally cover:

- navigation while the original field is disabled;
- navigation and same-origin `SetContent` replacement during asynchronous origin approval;
- hidden, disabled and read-only readiness, including a successful fill of the same element after readiness;
- a permanently hidden field and a detached/replaced matching field;
- cancellation before insertion, while approval is pending (including its later approval), during initial readiness and during readiness lost inside approval;
- actual child-frame origin approval and navigation of the owning child frame while the top-level URL remains unchanged.

Positive SSO still fills a separately approved password document. A `RunOneAsync` regression verifies that an interrupted bound insertion returns attention for the original workflow and stops follow-up actions even when the replacement document contains no visible password challenge.

To compare against a separately retained, pre-fix engine **without replacing production source**, the existing source override supports:

```sh
dotnet run --project tests/runtime/RuntimeTests.csproj -p:AutomationEngineSource=/absolute/path/to/AutomationEngine.before.cs -- --document-bound-only
```

Rebuild without the override before reporting final results. Keep RED output separate from GREEN output. Register navigation observation before starting the delayed fixture; observing only after a detached-handle failure can race Playwright's navigation/lifecycle observer.

## Security and verification boundary

The fix pins each approval and fill to an element, its owning frame and its document. A stale handle is never replaced with a newly resolved locator. Readiness and approval awaits are cancellation-aware and revalidated before a handle fill is dispatched; cancellation never starts a deferred secret-bearing fill from those waits. Playwright's dispatched `ElementHandle.FillAsync` operation itself has no cancellation-token API and is awaited to completion, not abandoned with `WaitAsync`.

This is **not protection against malicious JavaScript already executing in an approved origin**, which can observe or manipulate that origin's fields. It does not make subsequent submit/recorded workflow actions document-bound. Those are outside this credential-insertion repair.

Linux Chromium runs and Windows cross-compilation do not establish native WPF, Windows DPAPI, installed-browser or installer runtime behavior. Those native verification gates remain.
