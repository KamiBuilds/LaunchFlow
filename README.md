# LaunchFlow

LaunchFlow is a Windows desktop application for organizing website workflows, launching browser windows on selected monitors, and managing local encrypted credentials.

## Downloads

Once a Windows release has built successfully, download it from [LaunchFlow Releases](https://github.com/KamiBuilds/LaunchFlow/releases/latest). The universal installer is intended to check GitHub Releases and download verified updates without requiring users to install Visual Studio or the .NET SDK.

## Building from source

On Windows, run `BUILD-INSTALLER.cmd` with the .NET 8 SDK and Inno Setup. Outputs are written to `dist/`. The GitHub Actions release workflow is configured to build the app installer and universal installer from a version tag.

## Privacy and security

Local password databases, browser profiles, exported vaults, signing certificates and secrets must never be committed. These are development builds; test them on Windows before distributing installers.

## Repository status

The Windows release workflow has been installed. The full application source must be pushed before builds can succeed.
