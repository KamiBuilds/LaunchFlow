# Update existing LaunchFlow to v0.7.8

1. Keep your existing `%LOCALAPPDATA%\LaunchFlow` folder (do not delete `launchflow.db`).
2. On a Windows build PC, run `BUILD-INSTALLER.cmd`, then run `dist\LaunchFlow-Setup-v0.7.8-x64.exe`.
3. Exit LaunchFlow from the tray when Setup asks.
4. Start v0.7.8 and enter your master password. Older v0.6.x vaults unlock as usual; credential rows migrate automatically. v0.5.x still migrates once with the old master password under the same Windows account.

## v0.7.8 source repair note

This release repairs a v0.7.5 build error and three compiler warnings. It does not intentionally alter the encrypted database format. Exit LaunchFlow from its system tray before running the installer, then choose Update. For the full source rather than a ready-to-run .exe, build on a Windows PC using `BUILD-INSTALLER.cmd`.
