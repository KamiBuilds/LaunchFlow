# Recorder behavioral regression harness

Runs the linked production `TeachRecorder*.cs`, injected JavaScript, `VisualRecorderWindow.xaml.cs`, URL validation and the engine's step executor against real headless Playwright Chromium on .NET 8. WPF rendering, dialogs, chrome and timer scheduling are stubbed. Credential access, persistent-profile opening and native monitor operations fail if called. Window event handlers, queues, save/cancel transitions and click replay use production code. HTTPS-shaped fixtures are fulfilled by local Playwright routes; no external websites, real credentials or inference APIs are used.

```bash
DOTNET=/root/.cache/launchflow/dotnet/dotnet bash tests/recorder/run.sh
```

The development SDK used for the rescue is 8.0.427. CLI/NuGet/browser state defaults to `$HOME/.cache/launchflow`, with environment overrides supported. `PLAYWRIGHT_BROWSERS_PATH` defaults to `$HOME/.cache/launchflow/recorder-browsers`. Install the matching Chromium only if it is missing:

```bash
PLAYWRIGHT_BROWSERS_PATH="$HOME/.cache/launchflow/recorder-browsers" \
/root/.hermes/node/bin/node \
  "$HOME/.cache/launchflow/nuget/microsoft.playwright/1.63.0/.playwright/package/cli.js" \
  install chromium
```

This is a developer regression harness, not an end-user installation requirement.

## Administrator policy

The harness also links the actual `EnterprisePolicy.cs`, with a scoped live-value provider installed **before** Playwright creation so native callbacks observe policy changes. The default suite runs all 58 existing recorder checks plus 35 policy checks (93 total), not a policy-only subset. Construction/start, native acceptance, Drain/displayed-click provenance, manual editing/current URL capture, and Save are guarded. See [BOTH-RECORDER-POLICY.md](../../docs/BOTH-RECORDER-POLICY.md) for the contract, RED/GREEN evidence, fixture-preservation audit and native/integration limits.

Capture fixtures use locally fulfilled HTTP(S) navigation instead of `data:` URLs; all original assertions remain unchanged. The iframe fixture uses a routed HTTP(S) child rather than `srcdoc`. There is no production policy bypass for fixtures.

## Capture contract

- One shared binding and init script live for each browser context, not each recorder window. Closed sessions detach synchronously. A semaphore serializes installation/state updates; versioned snapshots and per-session epochs reject stale updates/events. No per-window init-script accumulation or binding removal/reinstallation race is required.
- Context-wide installation survives normal navigation and instruments existing pages/frames and future pages, real `window.open` popups, and iframes. **Persistence is deliberately limited to the original page's main frame.** Popup/other-page/frame actions are rejected and produce a fixed UI warning, rather than saving selectors against the wrong replay target. No model or engine fields are changed.
- Capture runs at the completed click's capture phase, before a click handler can remove the target. A pointer press alone is not persisted; dragging away, cancellation or target replacement must not teach a phantom button activation. Keyboard activation is captured without duplicating pointer clicks. Navigation/removal triggered before a completed click is unsupported rather than guessed.
- Editable fields are excluded. Automatic selectors use structural CSS only: no text, labels, IDs, ARIA values, typed text, or field values. The native binding also rejects nonstructural selectors. This is not isolation against a malicious page that can fabricate otherwise-valid structural events.
- Save synchronously freezes admission and drains actions already delivered to the application. It does not guess transport completion with a delay. Messages still in transit at the freeze may be dropped. Cancel/Close detach the session and discard unsaved actions; later continuations cannot resurrect capture or alter a saved snapshot. An empty Save resumes the previous active state. Closing the original browser page also prevents further capture/resume, but keeps already admitted actions available for Save.
- Explicit **Add current page** retains the latest-open-tab URL behavior from v0.7.19. It creates a Navigate step, not a popup-target binding. This seam now uses the real engine URL validator; the parent runtime suite covers validation failures more broadly.

Coverage includes navigation, simultaneous windows, pause across navigation, stale-event rejection, repeated sessions, concurrent initialization, detached-session snapshots, input/password exclusion, echoed text/ID exclusion, malformed and forged binding input, real pointer/keyboard events, popup/iframe target rejection, open-shadow-root rejection, and real window-controller startup/toggle/save/cancel/empty-save/remove/paused-drain races.

`-- --binding-api` prints installed API signatures without starting a browser. Microsoft.Playwright 1.63.0 returns `Task<IAsyncDisposable>` from `ExposeBindingAsync`; generic binding registration in the original source was not a compile defect.

`-- --legacy-script path/to/script.js` exercises a captured original script with binding `__testRecorder` and checks echoed-input privacy. The original script fails because it stores labels/IDs. Historical and resumed RED/GREEN evidence is kept outside Git under `/root/projects/launchflow-evidence/recorder`, with distinct `resume-*` logs. The first resumed command used an incorrect SDK executable path; it is retained as a prerequisite failure, not product RED. `resume-extended-first-run-01.log` also retains a failed SetContent popup fixture: document replacement removes document listeners without navigation/init-script rerun. The corrected actual-popup test uses normal navigation. The initial WPF cross-build exposed and fixed the WinForms/Playwright `BindingSource` name collision.

## Integration and unresolved gates

Compared against parent snapshot `0df5185` (v0.7.19): preserve its context-wide initialization/new-page behavior, latest-tab URL selection, all XAML event handlers/control names, and 520x460 window size. UI copy now explicitly narrows capture to supported targets. The parent reconciled the two XAML/controller conflicts against that newer snapshot and reran the real browser suite and Windows-targeted build.

Integration added a real canceled-drag regression: the button was not activated, but the pointerdown implementation queued a click. `parent-canceled-gesture-RED.log` reproduces that defect; the completed-click fix passes in `parent-canceled-gesture-GREEN.log`. The old assertion demanding capture before any click completed was replaced with a negative control and a positive completed-click/removal check. The new saved-window-to-real-engine replay checks passed on their first run (`parent-engine-replay-characterization.log`); they are characterization evidence, not an invented RED.

No parent model/engine changes are required for this bounded slice. Full popup/frame replay would require a persisted, stable page target plus a frame-locator chain and engine resolution of those identities; storing a bare selector is insufficient. Those changes belong to the parent and are not implemented or claimed here.

Remaining gates/limitations:

- Native Windows WPF event ordering, modal dialogs, timers, browser focus, modeless owner behavior, and installer/upgrade smoke. Linux controller seam tests and Windows-targeted cross-compilation are not native Windows runtime verification.
- Final integrated-parent gate after combining all reviewed slices. The harness now saves a real controller snapshot, reloads a locally routed page and replays its structural click through production `ExecuteStepAsync`, checking the intended control activates and its neighbor does not. This does not verify full `RunAsync`, persistent profiles, credential entry, or native WPF rendering.
- Structural selectors can become stale after DOM changes. Open-shadow-root events are rejected; closed-shadow-root retargeting cannot reliably be identified by document listeners and is unsupported. SVG/custom DOM selector compatibility needs a separate replay gate.
- `document.open`/`document.write` or Playwright SetContent-style document replacement without navigation can remove document listeners. Normal navigation is covered; automatic repair of arbitrary document replacement is not.
- Popup, iframe, shadow-DOM, and cross-tab replay are unsupported, not verified features. They must not be advertised as working based on context-wide observation alone.
