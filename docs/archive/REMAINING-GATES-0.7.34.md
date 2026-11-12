# Remaining gates after v0.7.34 UX clarity

This branch delivers the ChatGPT-side websites-first clarity pass. It does not authorize a stable release.

## Done
- Websites-first language across UI, login settings and create/delete flows
- Single login mode (AutoLogin derived from LoginMode)
- Post-create next-step guidance and Settings category hub
- Teach tab/MFA warnings and legacy workspace-group preservation

## Before release
1. Independently integrate the Hermes session/vault/profile lifetime changes.
2. Verify native Windows multi-monitor/tray/lock/installer behavior.
3. Sign the installer and nested DLL dependencies with a trusted publisher.
4. Review exact-SHA CI results; a green build alone is not release approval.
