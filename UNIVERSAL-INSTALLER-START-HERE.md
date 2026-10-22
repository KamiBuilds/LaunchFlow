# LaunchFlow Universal Installer — one executable for future updates

## What you get

After its **one-time developer build**, the file `LaunchFlow-Universal-Installer.exe` can be given to Windows 10/11 x64 users. It does NOT compile LaunchFlow or require Visual Studio Code, .NET SDK, or Inno Setup on their PCs.

Each time a user launches it:
1. It calls GitHub's public **latest release** endpoint for your configured repository.
2. It reads the version, installer asset download URL, file size and GitHub SHA-256 digest.
3. It shows whether LaunchFlow is installed and if a newer version exists.
4. When the user clicks **Download and install**, it downloads the setup, verifies size and SHA-256, and opens the normal LaunchFlow installer.
5. The normal Inno Setup installer updates existing app files in place. The local encrypted vault remains in the Windows user data folder.

**No GitHub repository is configured yet.** The universal installer cannot download a release until you have published at least one stable GitHub Release. A zip attached in ChatGPT is not an online update feed.

## One-time developer setup

1. Create a **public GitHub repository** for LaunchFlow (`OWNER/REPO`). Only publish source you are comfortable sharing. NEVER upload `launchflow.db`, browser profiles, exported credentials, key files, signing certificates, or passwords.
2. Upload or push the supplied project directory to that repository, preserving `.github/workflows/` and `bootstrapper/`.
3. To create an official downloadable version, run the GitHub **Release Windows installer** Action with the version from `LaunchFlow/LaunchFlow.csproj` (or push a version tag). It builds both `LaunchFlow-Setup-vVERSION-x64.exe` and `LaunchFlow-Universal-Installer.exe` and attaches them to a GitHub Release.
4. Download `LaunchFlow-Universal-Installer.exe` **once** from the GitHub Release. Keep or share this file. Future versions only require a new Release, not a new universal installer build.

Alternatively, with the .NET 8 SDK installed on your own Windows developer PC, double-click `BUILD-UNIVERSAL-INSTALLER.cmd` and provide the GitHub repo `OWNER/REPO`. The standalone EXE will be created in `dist/`.

You can also use the **Build standalone Universal Installer** action in GitHub Actions to build/download the launcher without building a release first. In that case it will work once a stable GitHub Release with the expected setup asset is published.

## Publishing future updates

1. Edit code and increment the app's version in `LaunchFlow/LaunchFlow.csproj` and the installer version in `installer/LaunchFlow.iss` (or let the release workflow set the Inno version from the new Git tag).
2. Push your updated code to the same GitHub repository.
3. Run **Release Windows installer** (or create/push a new tag, e.g. `v0.7.9`).
4. GitHub Actions compiles the real setup and creates a published Release containing the versioned setup asset.

**The exact same LaunchFlow-Universal-Installer.exe will automatically discover the new release on its next launch.**

## Important security and limitations

- The launcher uses HTTPS and only accepts a versioned installer asset from the GitHub repository it was built for; it rejects files without a valid GitHub SHA-256 digest, unexpected download URLs, oversized files, or mismatched digests.
- GitHub SHA-256 verifies download integrity relative to GitHub metadata. **It is not a replacement for signing releases with a trusted publisher certificate.** Unsigned installers may show Windows SmartScreen warnings. For public distribution, code-sign your releases and protect the repository's release permissions.
- No unattended execution of a downloaded EXE: the user approves before download/launch and still sees the installer wizard.
- This launcher supports **public** GitHub Releases, Windows x64, and a `LaunchFlow-Setup-vVERSION-x64.exe` release asset. Private GitHub repos and enterprise update servers need a different authenticated update backend.
- A universal installer doesn't run as a Windows service and does not check for updates in the background. It checks when the EXE is opened.
- If GitHub is blocked by network policy, installation cannot proceed. Do not bypass organization security restrictions.
- End-user installers cannot be built in this non-Windows environment. The C# project must be published once on Windows or through the supplied Windows GitHub Actions workflow.

## Protecting LaunchFlow credentials when upgrading

The existing Inno Setup script's AppId is retained. It installs program files under the Windows user profile and stores encrypted vault data separately. It should update an existing installation in place without removing the vault. However, create an encrypted LaunchFlow backup before the first upgrade and test the migration on Windows.
