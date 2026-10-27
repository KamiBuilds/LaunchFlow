# LaunchFlow

> **`both-3` hardening branch — not release-approved.** Based on the `both-2` UI and feature improvements, with policy-dispatch and profile-compatibility repairs. Work is being regression-tested and independently reviewed; consult [the hardening checklist](docs/BOTH-3-HARDENING.md) for exact verification status. Native Windows desktop/DPAPI/install-upgrade-uninstall tests remain required. Use synthetic data or disposable copies, not production credentials. This branch does not authorize or publish a stable release.

LaunchFlow is a Windows desktop application for automated website workflows, secure local credential storage, and multi-monitor browser sessions.

## Install LaunchFlow

**Windows 10 or 11 · x64**

### [Download the LaunchFlow Universal Installer](https://github.com/KamiBuilds/LaunchFlow/releases/latest/download/LaunchFlow-Universal-Installer.exe)

1. Download and run `LaunchFlow-Universal-Installer.exe`.
2. Click **Download and install**. The installer fetches the latest stable LaunchFlow version from the official GitHub release, checks the published SHA-256 digest, and launches Windows Setup.
3. Complete setup and open LaunchFlow. Create your master password, choose startup behavior, and add your website workflows.

**For end users: one EXE, no ZIPs, no SDK, and no separate .NET runtime installation.** The Universal Installer is a standalone Windows EXE. It downloads the latest published stable Windows Setup, verifies the release's SHA-256 digest, and launches Setup. That setup includes LaunchFlow, the .NET runtime, and its application dependencies. An internet connection is needed for downloads and updates. Windows may display an unknown-publisher warning until the executables are digitally signed.

**Browser requirement:** Website automation uses an already-installed Microsoft Edge or Google Chrome browser. Edge is normally included in Windows 10/11. LaunchFlow does not silently install or change browsers, so a supported browser is needed to run website workflows.

The versioned `LaunchFlow-Setup-vX.Y.Z-x64.exe` release asset is the payload the universal installer downloads automatically; normal users should download only the universal installer.

This download becomes available once the first stable release is published. Draft releases are reserved for testing.

## Updates

Run the **same universal installer** again to find and install the latest stable version. The application also supports checking for newer versions. Existing encrypted vaults and workflow data are retained when upgrading.

## Quick start

1. Open LaunchFlow and use **+ New** to add a website.
2. Open **Passwords** if you want LaunchFlow to sign in using your saved credentials.
3. Select your website and click **Teach clicks** if it should click something after sign-in. Move between the browser and Teach window freely; recording starts automatically.
4. Click **Save clicks**, then **Test this site** to try the workflow.
5. Choose **Help** anytime for the four-step setup guide.

Teach never records typed text or passwords. Some websites need you to complete MFA yourself. Not every login can be fully automated.

## Development

The core application is in `LaunchFlow/`. The standalone updater is in `bootstrapper/`; Windows setup definitions are in `installer/`; build support is in `tools/`.

Pushes to `both-3`, `both-2`, `both`, `hermes` and `chatgpt`, plus pull requests, run automated checks and Windows installer builds. The [Windows workflow](https://github.com/KamiBuilds/LaunchFlow/actions/workflows/windows.yml) uploads build artifacts; it does not publish a stable release. Its Linux browser job tests linked production logic with explicit Windows platform seams, not the native desktop app.

For a manual build, choose **Run workflow** on the intended branch and leave `publish_release` disabled. Optional draft creation requires explicit `DRAFT-v<VERSION>@<full selected commit SHA>` confirmation and native Windows test evidence. That path creates a **draft only**; stable publication is a separate human action after verification. The stable download above does not install this experimental branch.

Developers can use `BUILD-INSTALLER.cmd` to build locally. It checks for the .NET 8 SDK and Inno Setup, and can offer to install missing build tools using WinGet. **These developer tools are never needed on the end user's PC.** See [CHANGELOG.md](CHANGELOG.md) and [CODE-SIGNING.md](CODE-SIGNING.md) for development notes.

## Known testing limitations

Teach currently records clicks on its managed browser page. Popups and separate tabs may need manual steps. Keep credentials in **Passwords**, not Teach; it does not record typed inputs or passwords. The Universal Installer uses **published** stable GitHub Releases, not drafts.

## Privacy and security

LaunchFlow keeps vault data locally. Do not commit credentials, encrypted databases, browser sessions, exported profiles, logs or signing certificates. Installer builds must be tested before public distribution.
