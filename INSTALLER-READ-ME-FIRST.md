# LaunchFlow v0.7.8 — one-click builder with prerequisite installation

## Developer PC (build the setup)

1. Open this folder on Windows 10/11 x64.
2. Double-click `BUILD-INSTALLER.cmd`.
3. If .NET 8 SDK or Inno Setup is missing, approve the official WinGet install when asked.
4. Publishes LaunchFlow as a **self-contained Windows x64** app (runtime included, ReadyToRun) and creates `dist\LaunchFlow-Setup-v0.7.8-x64.exe`.

## End-user PC

Distribute **only** `LaunchFlow-Setup-v0.7.8-x64.exe`. See `FOR-END-USERS.md`.

People installing LaunchFlow do **not** need Visual Studio Code, .NET SDK, Inno Setup, WinGet, or KeePassXC. Edge or Chrome should already be on the machine for automation. The built-in encrypted vault is local and self-contained.
