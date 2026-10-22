# LaunchFlow

LaunchFlow is a Windows desktop workspace automation application. It manages website workflows, encrypted sign-in credentials, monitor placement, and configurable automation triggers.

## Downloads

Once a verified Windows build is published, get the latest setup from [Releases](https://github.com/KamiBuilds/LaunchFlow/releases/latest). The **LaunchFlow Universal Installer** downloads the latest published setup without requiring Visual Studio or the .NET SDK on an end-user machine.

## Build from source

On Windows with .NET 8 SDK and Inno Setup available, run `BUILD-INSTALLER.cmd`. The resulting installer is written to `dist/`.

## Data and security

LaunchFlow stores your encrypted vault locally. Never commit databases, profiles, `.launchflow` exports, credentials, signing keys, or browser session files.

## Current state

This repository is under development. Installer and runtime tests should be completed on Windows before distributing release builds.
