# Code signing LaunchFlow (Authenticode)

Windows SmartScreen trusts installers signed with a **purchased** code-signing certificate that has reputation (or an EV / Azure Trusted Signing identity). Unsigned or self-signed builds still install; users may see “Windows protected your PC.”

## How to get a real certificate (SmartScreen)

1. **Azure Trusted Signing** (often simplest for indie/small publishers)  
   Azure subscription → Trusted Signing account → identity validation → sign in CI.  
   Docs: https://learn.microsoft.com/en-us/azure/trusted-signing/

2. **Standard or EV code-signing certificate from a CA**  
   Buy from DigiCert, Sectigo, SSL.com, etc. Most issue **hardware-token / cloud-HSM** certs (not a loose `.pfx` in email).  
   **EV** gets SmartScreen reputation faster; standard OV builds reputation as people install.

3. **Wire it into LaunchFlow**  
   - Local: set `LAUNCHFLOW_SIGN_PFX` + `LAUNCHFLOW_SIGN_PFX_PASSWORD` (or thumbprint), run `BUILD-INSTALLER.ps1`  
   - GitHub: secrets `LAUNCHFLOW_SIGN_PFX_BASE64` + `LAUNCHFLOW_SIGN_PFX_PASSWORD`, then **Release Windows installer**  
   - Self-signed (`tools\New-CodeSigningCert.ps1`) is **your PC only** — not for public downloads  

Expect identity verification (business docs or personal ID). Cost varies by CA and EV vs standard; Azure Trusted Signing is pay-as-you-go after validation.

## Quick local test (self-signed)

On the Windows build PC:

```powershell
.\tools\New-CodeSigningCert.ps1
$env:LAUNCHFLOW_SIGN_PFX = (Resolve-Path .\certs\launchflow-codesign.pfx).Path
$env:LAUNCHFLOW_SIGN_PFX_PASSWORD = '<password>'
.\BUILD-INSTALLER.ps1 -Yes
```

Self-signed is for **your machine only**. Do not expect SmartScreen to trust it for other PCs.

## Production / GitHub Actions

1. Obtain an Authenticode certificate (PFX) from a CA, or use Azure Trusted Signing / an EV USB token.
2. Add repository secrets:
   - `LAUNCHFLOW_SIGN_PFX_BASE64` — base64 of the `.pfx` file  
     (`[Convert]::ToBase64String([IO.File]::ReadAllBytes('cert.pfx'))`)
   - `LAUNCHFLOW_SIGN_PFX_PASSWORD` — PFX password
3. Run **Release Windows installer** (or push tag `v0.7.8`).

The workflow writes the PFX to a temp path and sets `LAUNCHFLOW_SIGN_PFX` / `LAUNCHFLOW_SIGN_PFX_PASSWORD` for `BUILD-INSTALLER.ps1`.

## Environment variables

| Variable | Purpose |
|----------|---------|
| `LAUNCHFLOW_SIGN_PFX` | Path to `.pfx` |
| `LAUNCHFLOW_SIGN_PFX_PASSWORD` | PFX password |
| `LAUNCHFLOW_SIGN_THUMBPRINT` | SHA1 of a cert already in the Windows cert store |
| `LAUNCHFLOW_SIGN_SUBJECT` | Subject name match (`/n`), e.g. `LaunchFlow` |
| `LAUNCHFLOW_SIGNTOOL` | Optional full path to `signtool.exe` |
| `LAUNCHFLOW_SIGN_TIMESTAMP_URL` | Optional RFC 3161 URL (default DigiCert) |

Priority: thumbprint → subject → PFX.

## What gets signed

When a cert is configured and `signtool.exe` is available (Windows SDK):

1. **`publish\LaunchFlow.exe`** before Inno packs the app  
2. **Setup.exe and the uninstaller** via Inno Setup `SignTool=lfsign` (`/Slfsign=` from the builder)  
3. A final pass on `dist\LaunchFlow-Setup-*.exe` if Inno did not sign it

## Requirements on the builder PC

- Windows 10/11 x64  
- .NET 8 SDK + Inno Setup (via `BUILD-INSTALLER`)  
- **Windows SDK Signing Tools** (`signtool.exe`) — install “Windows SDK” or the “Signing Tools for Desktop Apps” workload  

Helper scripts live under `tools\`:

- `New-CodeSigningCert.ps1` — local test certificate  
- `Sign-LaunchFlow.ps1` — shared signing helpers (dot-sourced by the builder)
