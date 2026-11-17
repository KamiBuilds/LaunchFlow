# Recorder behavioral regression harness

Runs the linked production `TeachRecorder*.cs`, injected JavaScript, `VisualRecorderWindow.xaml.cs`, URL validation and the engine's step executor against real headless Playwright Chromium on .NET 8. WPF rendering, dialogs, chrome and timer scheduling are stubbed. Credential access, persistent-profile opening and native monitor operations fail if called. Window event handlers, queues, save/cancel transitions and click replay use production code. HTTPS-shaped fixtures are fulfilled by local Playwright routes; no external websites, real credentials or inference APIs are used.

```bash
dotnet run --project tests/recorder/RecorderTests.csproj -c Release
```

Install the matching Playwright Chromium for this harness if it is missing:

```bash
pwsh Microsoft.Playwright/1.63.0/.playwright/package/bin/playwright.ps1 install chromium
```

This is a developer regression harness, not an end-user installation requirement.

## Administrator policy

The harness also links the actual `EnterprisePolicy.cs`, with a scoped live-value provider installed **before** Playwright creation so native callbacks observe policy changes. Construction/start, Drain/displayed-click provenance, manual editing/current URL capture, and Save are guarded.

Capture fixtures use locally fulfilled HTTP(S) navigation instead of `data:` URLs. The iframe fixture uses a routed HTTP(S) child rather than `srcdoc`. There is no production policy bypass for fixtures.

## Capture contract

- One shared binding and init script live for each browser context, not each recorder window. Closed sessions detach synchronously.
- Capture runs at the completed click's capture phase. Editable fields are excluded. Automatic selectors use structural CSS only.
- Save synchronously freezes admission and drains actions already delivered to the application. Cancel/Close detach the session and discard unsaved actions.
- Explicit **Add current page** creates a Navigate step using the real engine URL validator and refuses secret-bearing query/fragment/userinfo URLs.

Remaining gates: native Windows WPF event ordering, modal dialogs, browser focus, modeless owner behavior, and installer/upgrade smoke. Linux controller seam tests and Windows-targeted cross-compilation are not native Windows runtime verification.
