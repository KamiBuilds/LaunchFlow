# LaunchFlow v0.7.8 — Saving sign-in credentials

Open **Passwords**, select a workflow, and unlock the vault if prompted.

- Both **Username/email** and **Password** are masked with bullets. There is **no reveal button**.
- Enter both fields to save a first login; click **Save credentials**.
- To update an existing login, enter a *replacement* username/email and/or password. Leave a field empty to keep that saved value; click **Save credentials**.
- Previous login values are never shown or prefilled. A saved-credentials status indicates a login exists.
- Merely switching tabs or clicking the main Save button does not update login credentials.
- New Workflow also masks the username/email during entry.

Masking reduces accidental screen disclosure, but vault encryption and Windows session security protect data at rest. The encrypted database still requires the master password to unlock.

## Update

1. Exit old LaunchFlow from the system tray, and make an encrypted backup.
2. Unzip this project on Windows and double-click **BUILD-INSTALLER.cmd**.
3. Run **dist\LaunchFlow-Setup-v0.7.8-x64.exe** to update in place.
4. Verify two-step login (e.g. ThousandEyes), then change a password using **Save credentials** and verify subsequent sign-in.

Windows compilation and live login are not verified in this environment.
