# Engine policy and live monitor integration

This is an additional integration slice on top of the saved engine lifecycle repair, not a native Windows or release approval.

## Enforced boundaries

- Public run admission refuses administrator-disabled automation before creating an active run.
- Configured start and destination URLs are authorized before browser/default-browser dispatch. Actual URLs are checked after initial navigation, recorded actions and final navigation. A blocked redirect is not successful completion.
- Credential lookup checks automation/fill/storage policy before requesting the vault or optional external lookup. The document-bound insertion helper retains its original owning element/frame/document identity checks and now rechecks administrator host/HTTPS/credential permission around readiness and approval. User approval cannot override administrator denial.
- Ordinary recorded Click/Fill actions resolve a single element handle and authorize its owning document. They cannot use a live locator to retarget a replacement document after that check. Recorded Fill rechecks permission and cancellation after asynchronous resolution, before dispatch. Dispatched fills are awaited, not abandoned with cancellation wrappers.
- Teach acquisition checks automation/Teach restrictions and the configured start URL before admission and again after waiting, then checks the actual returned page URL. Ongoing capture and UI restrictions are separate integration slices.
- Persistent-launch results are owned before policy is rechecked; revocation closes an unclaimed result without inspecting or publishing its pages. Failed cleanup retains the existing fail-closed ownership barrier.
- Browser window placement resolves the saved device identity against current monitor enumeration at dispatch. An absent named display falls back to primary without rewriting the stored preference.

## Evidence

`tests/runtime` links the actual engine, policy parser and monitor resolver. Browser pages, local HTTP redirects, element readiness and input assertions are exercised using real Chromium. Credential retrieval and native screen enumeration remain explicit seams. Monitor tests capture production CDP command payloads; they are not Windows monitor-placement evidence.

Run:

```sh
PLAYWRIGHT_BROWSERS_PATH=/path/to/development/browsers dotnet run --project tests/runtime/RuntimeTests.csproj -warnaserror
dotnet run --project tests/profiles/ProfileTests.csproj -warnaserror
DOTNET=dotnet bash tests/policy/run.sh -c Release
dotnet build LaunchFlow/LaunchFlow.csproj -p:EnableWindowsTargeting=true -p:PublishReadyToRun=false -warnaserror
```

The focused switches `--policy-only` and `--monitor-only` exercise these additions. Existing document-bound credential, lifecycle, dispatched-fill, login-health, KeePass, busy-profile and restore guards remain in the full suite. The policy/vault suite separately exercises actual encrypted SQLite storage and transfer boundaries with synthetic data.

Local numbered evidence is retained outside the repository under `both/engine-policy`. Initial mocked HTTP-redirect attempts reached Chromium's DNS/error document because followed redirect requests can bypass route interception; those are retained fixture failures, not security RED or GREEN. Corrected owned-loopback HTTP redirect fixtures were replayed against retained pre-integration engine source to establish an actual failing baseline. All listeners close at fixture disposal. Later RED/GREEN pairs cover credential flags/HTTPS, owning-frame denial, asynchronous approval/retrieval revocation, recorded-step restrictions, launch ownership and monitor preference.

## CI fixture synchronization correction

The earlier pushed engine checkpoint's Linux CI exposed a failure in the aborted-workflow fixture: a 200 ms delay plus an incomplete task did not prove that credential binding had started. Playwright initialization/window placement could still be pending, so replacing the document first legitimately selected the replacement as the initial document.

The maintained fixture now deliberately delays CDP placement, observes the original input during bound-element resolution, then performs the same replacement and original no-fill/no-follow-up/manual-attention assertions. The slow-start fixture reproduced the old synchronization failure; corrected observation passed four sequential focused runs. An initial test-only JavaScript local-name shadowing error is retained separately, not claimed as an application defect. This changes test synchronization, not production credential logic.

## Limits and remaining gates

Administrator policy uses cooperative snapshots, not an atomic transaction with registry changes. Already-dispatched browser operations cannot be recalled, and browser scripts/network activity and external default-browser windows are not an OS/network sandbox. Keyboard actions retain the existing page-focused behavior. The credential binding does not protect against malicious scripts already executing in an approved origin. Arbitrary untrusted in-process code is not contained.

Native Windows registry, WPF/session-lock ordering, real DPAPI, monitor topology, installed browsers and clean-PC installer/upgrade/uninstall verification remain separate gates. Final UI/recorder policy wiring and independent review of the complete combined tree are required. No stable release is authorized by these checks.
