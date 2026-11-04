# LaunchFlow — generic Windows deployment contract (candidate)

This is a **platform-independent packaging input**, not an organization-specific procedure or a claim of enterprise approval. Product version: **0.7.46**. The versioned, self-contained **offline** `LaunchFlow-Setup-v0.7.46-x64.exe` is the packaging endpoint. `LaunchFlow-Universal-Installer.exe` is an optional interactive download bootstrapper, **not** an approved headless deployment endpoint.

## Supported scope and identity

- **Per-user only**: `PrivilegesRequired=lowest`, default program path `%LOCALAPPDATA%\Programs\LaunchFlow`. Registry registration and file association use **HKCU**. Do not use a service/SYSTEM identity to install into the wrong user's profile.
- **Machine-wide, /ALLUSERS and a privileged deployment broker are not supported or verified**. LaunchFlow does not enable `PrivilegesRequiredOverridesAllowed`.
- Encrypted user data and backups are separate from program files under `%LOCALAPPDATA%\LaunchFlow`; upgrades and unattended uninstall must retain that directory. Uninstalling does not migrate or unlock another user's data.
- The current same-session mutex is `LaunchFlow.Windows.SingleInstance`. It does **not** constitute proof that another interactive user's process can be controlled. An unsafe busy or inaccessible process must block deployment, not be killed.
- Inno Setup AppId **`{9B1A7C15-093B-4DBA-BDA7-BF43A2E67320}`** must remain unchanged for in-place upgrades.

## Installer invocation

Use a process wrapper that **waits** for the child and captures its actual exit code. Illustrative command executed in the intended user's supported context:

```cmd
LaunchFlow-Setup-v0.7.46-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LOG
```

- `/VERYSILENT` skips wizard/progress UI. `/SUPPRESSMSGBOXES` suppresses Inno-owned eligible messages, but not arbitrary Pascal-script `MsgBox`; LaunchFlow explicitly guards its custom setup errors with `WizardSilent`.
- `/NORESTART` prevents automated reboot. A silent run never starts LaunchFlow afterward (`skipifsilent` on the post-install Run entry). Browser installation/download and vault creation are **not** performed silently.
- Existing LaunchFlow is asked to exit via `--exit-for-install`. If it does not complete safe shutdown (including vault/profile handling), installation aborts; no force kill.
- For a custom log location, provide a writable `/LOG="path"`; do not depend on parsing Inno's prose log for detection. An invalid path or framework startup error may have vendor-specific behavior that needs native verification.
- Inno documents setup exit codes **0** (completed/help) and **nonzero** (not completed); a wrapper must verify installed identity/version on top of the code, preserve unknown codes, and not interpret a launch as completed installation. No MSI-specific exit-code mapping is implied.

## Uninstall

Run the **registered uninstaller belonging to that user's installation**, such as `%LOCALAPPDATA%\Programs\LaunchFlow\unins000.exe` when this filename is actually present:

```cmd
unins000.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

- The silent uninstall explicitly skips the optional vault-delete question and **preserves encrypted data, workflows and backups**.
- If the application mutex indicates an active instance, uninstall returns an abort outcome rather than removing program files from a running session. Interactive uninstall still allows explicit, informed data deletion; never grant that consent from a silent deployment wrapper.
- A clean reinstall under the same supported user identity should find the preserved vault. Validate this with disposable synthetic data.
- Never recursively delete user profiles or synthesize a data-wipe command as part of deployment.

## Detection and dependencies

Detection must check **HKCU** under `Software\Microsoft\Windows\CurrentVersion\Uninstall\{9B1A7C15-093B-4DBA-BDA7-BF43A2E67320}_is1`, its `DisplayVersion` and `InstallLocation`, and that the expected `LaunchFlow.exe` actually exists in the intended user context. A SYSTEM-process HKCU cannot prove the interactive user's installation. The installer is x64; verify runtime architecture and installed build identity independently.

The builder publishes self-contained .NET 8 WPF files and bundled Playwright driver/native dependencies. A **supported browser** remains an external, user-consented/policy-controlled dependency. The installer does not install a browser, developer SDK, Inno Setup or signing tools on the target.

Artifact SHA-256 must be recorded after signing/packaging. A checksum alone does not establish publisher trust. Optional publisher signing and application-control policy authorization remain packaging/security decisions; neither guarantees a controlled workstation will permit every DLL, driver or browser child process.

## Acceptance status

**Source checks and Inno compilation are not unattended acceptance.** Before claiming enterprise readiness, run clean Windows per-user install, upgrade with synthetic vault/backup, dirty/busy-app failure, silent uninstall and reinstall, denied permissions, error/exit-code capture, browser presence/denial, and a permitted target-device execution test. Preserve redacted artifact SHA and platform/build context. Other user sessions, SYSTEM invocation and machine-wide install remain unsupported unless explicitly designed and separately approved.

Official Inno documentation:
- https://jrsoftware.org/ishelp/topic_setupcmdline.htm
- https://jrsoftware.org/ishelp/topic_uninstcmdline.htm
- https://jrsoftware.org/ishelp/topic_isxfunc_wizardsilent.htm
- https://jrsoftware.org/ishelp/topic_isxfunc_uninstallsilent.htm
- https://jrsoftware.org/ishelp/topic_setupexitcodes.htm
