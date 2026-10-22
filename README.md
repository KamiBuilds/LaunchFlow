# LaunchFlow

Windows desktop application for website workflows, encrypted local credentials, multiple-monitor browser placement, and configurable Windows triggers.

## Download LaunchFlow

After the first release succeeds, download the **[Universal Installer](https://github.com/KamiBuilds/LaunchFlow/releases/latest)**. It checks the latest published release whenever you run it; everyday users do **not** need Visual Studio, Inno Setup, or the .NET SDK.

Until the first published release exists, use **[Actions → Windows build and release](https://github.com/KamiBuilds/LaunchFlow/actions/workflows/windows.yml)** to find the latest development build artifact (GitHub sign-in may be required). Development artifacts are not automatically offered by the universal updater.

## Automated builds and releases

- **Each push to `main`** automatically builds the Windows setup executable, universal installer, and a **source ZIP**, then uploads all three as a downloadable Actions artifact.
- **Each version tag (`v0.7.8`, `v0.7.9`, etc.)** builds the same files and publishes a GitHub Release that the universal installer can download.
- **To publish without creating a tag manually**, open the Windows build and release workflow, click **Run workflow**, check **Publish a GitHub Release**, and run it. The release uses the version in `LaunchFlow/LaunchFlow.csproj`.
- Creating a ZIP inside a ChatGPT conversation does **not** automatically push it to GitHub. Changes must be committed to the repository; once committed, GitHub handles packaging.

## Source layout

| Path | Purpose |
| --- | --- |
| `LaunchFlow/` | Windows WPF application, encryption vault, and browser automation |
| `bootstrapper/` | One-time universal update installer |
| `installer/` | Inno Setup packaging configuration |
| `tools/` | Signing and developer utilities |
| `.github/workflows/windows.yml` | Single Windows build/release workflow |
| `BUILD-INSTALLER.cmd` | Optional local Windows installer build for developers |
| `BUILD-INSTALLER.ps1`, `BUILD-UNIVERSAL-INSTALLER.ps1` | Developer build scripts |
| `CHANGELOG.md`, `CODE-SIGNING.md` | Release history and signing instructions |

## Building locally

On Windows, install the .NET 8 SDK and Inno Setup, or allow the developer build script to offer those dependencies. Run `BUILD-INSTALLER.cmd`. The setup EXE will be under `dist/`. Ordinary users should use a published installer instead of compiling the project.

## Privacy and release safety

Never push local vault databases, user profiles, browser cookies, exported credentials, secrets, signing certificates, or logs to this public repository. The `.gitignore` excludes common sensitive files. Always inspect changes before publishing. Build automation is not a security audit or a replacement for testing installation, Windows locking, encryption, and login flows.

**Status:** Development project. Releases are unsigned until a trusted code-signing certificate is configured. Windows may show an unknown-publisher prompt.
