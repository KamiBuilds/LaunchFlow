# LaunchFlow managed Windows deployment (development)

This is implementation guidance, **not a government security accreditation**.
The app remains local-only. Administrators must validate Windows signing,
endpoint management, accessibility, retention, privacy, authorization and MFA
requirements before any production deployment.

## Machine policies

Machine policy lives at `HKLM\SOFTWARE\Policies\LaunchFlow` (not HKCU).
A standard Windows account should not be able to modify this registry key.
Use Intune, Group Policy or another authorized configuration management system.

| Registry value | Type | Behavior when set to 1 |
|---|---|---|
| DisableAutomation | DWORD | Blocks running browser workflows |
| DisableCredentialFill | DWORD | Blocks automatic password/username filling |
| DisableCredentialStorage | DWORD | Blocks saving and importing credentials in the vault; existing stored secrets are not erased automatically |
| DisableExport | DWORD | Blocks creating encrypted .launchflow exports |
| DisableTeach | DWORD | Blocks recording browser interactions |
| RequireHttpsForCredentials | DWORD | Blocks filling credentials on non-HTTPS pages |
| AllowedHosts | REG_SZ | Comma-separated exact hostnames (no wildcards). Restricts navigation in LaunchFlow |

Absent keys keep personal-mode behavior. Failed policy reads **deny** the
managed actions, to avoid an accidental bypass. Exact host allowlisting does
not equate to web content trust; monitor redirected origins and SSO providers.

**Example (administrator PowerShell, intentionally not run by installer)**

```powershell
New-Item 'HKLM:\SOFTWARE\Policies\LaunchFlow' -Force | Out-Null
New-ItemProperty 'HKLM:\SOFTWARE\Policies\LaunchFlow' DisableCredentialFill -PropertyType DWord -Value 1 -Force
New-ItemProperty 'HKLM:\SOFTWARE\Policies\LaunchFlow' RequireHttpsForCredentials -PropertyType DWord -Value 1 -Force
```

## Authentication & trust

Prefer organizational SSO with user-completed MFA. No component may bypass
conditional access, MFA, endpoint policy, or app allowlisting. Do not record
secret values in Teach sessions. Credential-based automation must check the
exact current page origin before each fill or submit.

## Deployment validation checklist

- Build from an immutable revision, verify provenance, sign binaries and installer.
- Run the Windows CI checks and manually test fresh install, in-place upgrade
  and uninstall retaining or explicitly deleting vault data.
- Test locked and unlocked workstations, duplicate events, sleep/resume, 
  browser failures and disconnected displays.
- Test Windows screen readers, keyboard-only interaction, 200% DPI and
  high-contrast mode; verify Dutch/English user-facing language.
- Validate policy enforcement as a standard user and on managed profiles.
- Establish retention, support, incident response and security update ownership.

No broad government compliance claim is made by this repository.
