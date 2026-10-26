# Smartest App Developer — engineering status

Base: v0.7.19 / `chatgpt`. This branch is an **experimental** implementation.
Do not merge into production until the automated Windows job and manual
acceptance tests below have passed. Existing user vaults should be backed up
using the encrypted Export flow before testing a development build.

## Ten workstreams

1. **Enterprise controls — initial implementation.** Machine policy under
   `HKLM\SOFTWARE\Policies\LaunchFlow` gates runs, Teach, credential filling and storage,
   import/export, and exact-host navigation. No remote management plane or full
   organizational role-based policy editor yet.
2. **Self-healing — safe detection/repair foundation.** Broken action stops
   that workflow, reports the affected action and invites human repair.
   No speculative automatic re-clicking or retries of non-idempotent actions.
3. **Workspace profiles — usable initial implementation.** Group enabled
   workflows under a name and run a selected profile. Monitor placement
   is identified by display device name with fallback to the primary display.
   Detailed layouts and per-workspace schedules remain future work.
4. **Authentication — SSO-aware handling.** Automatic credential entry
   respects policy and current origins; SSO can be selected for manually
   completing organization-managed login and MFA. No bypass of identity
   provider controls or claim of enterprise federated-login certification.
5. **Execution reliability — first hardening pass.** Linked cancellation,
   state notifications, safe failure handling, session cleanup and an
   asynchronous lock path. Needs real timing/race tests on Windows.
6. **Teach — privacy and selector improvements.** Capture remains click-only,
   prefers stable test IDs, avoids page labels and value capture, and checks
   managed host rules for recording frames. Complex multi-step flow authoring
   remains a roadmap item.
7. **Diagnostic privacy — initial implementation.** Local size-rotated
   event logs use restricted identifiers and exception types, not exception
   messages, stack traces, credentials, internal URLs or form values.
8. **Deployment — foundations and guide.** Existing Inno Setup builder,
   managed policy reference, existing signing documentation and Windows CI.
   Silent deployment and actual Intune/ConfigMgr testing are **not complete**.
9. **Accessibility/localization — initial implementation.** English/Dutch
   preference and translated primary navigation, accessible names and live
   workflow status. Full screen-reader, contrast, keyboard and translation
   audits are **not complete**.
10. **Quality assurance — new automated tests.** Windows PR compile plus
    policy/workspace smoke tests and static regression gates. Native
    GUI tests, browser tests, threat modeling and full installer validation
    are **not complete**.

## Essential manual acceptance tests

- Fresh install plus upgrade against a copied **test** vault. No data loss.
- Lock Windows mid-login for more than the configured timeout; confirm the
  vault is sealed and no pending browser action is able to fill a password.
- Repeated unlocks, cancellation while a page is loading, and a browser
  crash must not produce duplicate unattended submissions or sessions.
- Redirect a mock login to another origin; verify no secret is filled until
  explicit origin approval. Repeat with `RequireHttpsForCredentials=1`.
- Test policy with a standard Windows user: disallow a site, Teach, credential
  fill and import/export; confirm no UI route bypasses the policy.
- Disconnect/reconnect a second monitor while a workspace is configured.
- Teach a mock page with private data: recorded steps and diagnostics must not
  contain field values or dynamic page labels.
- Run using Edge, Chrome and Firefox under permitted endpoint policies.
- Run keyboard-only, NVDA/Narrator, 200% DPI and Windows high-contrast tests.
- Build and run `tests/LaunchFlow.SafetySmokeTests`, run the source verifier,
  and test the generated installer before tagging a release.

These tests and any department-specific security review are release blockers.
