# LaunchFlow

LaunchFlow is a Windows desktop application for automated website workflows, secure local credential storage, and multi-monitor browser sessions.

## Install LaunchFlow

**Windows 10 or 11 · x64**

### [Download the LaunchFlow Universal Installer](https://github.com/KamiBuilds/LaunchFlow/releases/latest/download/LaunchFlow-Universal-Installer.exe)

1. Download and run `LaunchFlow-Universal-Installer.exe`.
2. Click **Download and install**. The installer fetches the latest stable LaunchFlow version from the official GitHub release, checks the published SHA-256 digest, and launches Windows Setup.
3. Complete setup and open LaunchFlow. Create your master password, choose startup behavior, and add your website workflows.

**No source archives, Git, Visual Studio, or developer SDKs are needed.** The first installation requires internet access. Windows may display an unknown-publisher warning until the release is digitally signed.

The versioned `LaunchFlow-Setup-vX.Y.Z-x64.exe` release asset is the payload the universal installer downloads automatically; normal users should download only the universal installer.

This download becomes available once the first stable release is published. Draft releases are reserved for testing.

## Updates

Run the **same universal installer** again to find and install the latest stable version. The application also supports checking for newer versions. Existing encrypted vaults and workflow data are retained when upgrading.

## Development

The core application is in `LaunchFlow/`. The standalone updater is in `bootstrapper/`; Windows setup definitions are in `installer/`; build support is in `tools/`.

Pushing commits to `main` automatically builds both installers. Downloadable development builds are available under [GitHub Actions](https://github.com/KamiBuilds/LaunchFlow/actions/workflows/windows.yml). A version tag (`v0.7.9`, for example) or a deliberate **Publish a GitHub Release** workflow run publishes them for end users.

Windows developers can use `BUILD-INSTALLER.cmd` with .NET 8 SDK and Inno Setup. See [CHANGELOG.md](CHANGELOG.md) and [CODE-SIGNING.md](CODE-SIGNING.md) for development notes.

## Privacy and security

LaunchFlow keeps vault data locally. Do not commit credentials, encrypted databases, browser sessions, exported profiles, logs or signing certificates. Installer builds must be tested before public distribution.
