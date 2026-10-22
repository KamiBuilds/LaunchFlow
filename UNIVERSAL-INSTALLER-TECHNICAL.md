# Implementation notes

Launcher project: `bootstrapper/LaunchFlow.UniversalInstaller.csproj` (Windows Forms .NET 8).

The initial public repo address is embedded in `bootstrapper/release-source.txt` as an **embedded resource**, which is copied into the self-contained one-file .exe. The user cannot redirect the installer to an arbitrary server through the UI. The real setup executables are version-specific and published as GitHub Release assets.

GitHub endpoint: `https://api.github.com/repos/OWNER/REPO/releases/latest`. Expected asset: `LaunchFlow-Setup-v<version>-x64.exe`. For each release, GitHub API provides `digest: sha256:<hex>`; this is verified over the entire downloaded file before opening it.

The launcher looks up installed version using Inno Setup's per-user uninstall registry key (`9B1A7C15-093B-4DBA-BDA7-BF43A2E67320`). When the same or newer version is already installed, it offers to open the current application or reinstall the latest release.

Building the launcher is a developer step performed once. The release workflow publishes the app's normal installer on each release; regular users never need to build or install SDKs.

Hardening ideas for a public release: certificate publisher validation (require signed setup), rollbacks, localization, caches cleanup, and code-signing the universal launcher itself. Preserve the repository ownership and release signing trust across versions.
