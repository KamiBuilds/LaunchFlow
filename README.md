# LaunchFlow

> **v0.7.37 — architecture and source maintainability refactor preview (not release-approved).** Local Windows desktop automation with an encrypted credential vault. Native Windows verification and Hermes lifecycle integration remain required before a stable publish.

LaunchFlow opens your websites on the chosen screen, optionally signs in using local encrypted credentials, and replays recorded safe clicks.

## Requirements

Windows 10 or 11 (x64), with an installed supported browser. This source preview is not a published stable release.

## Quick start

1. In **Websites**, click **Add website**. Choose its URL, browser, and monitor.
2. If the website needs login details, enable sign-in or open **Settings → Passwords** after creation. Otherwise choose **No login required**.
3. Select the website and use **Record clicks** only if you want automated actions. Complete MFA yourself.
4. Click **Test this site**, then use **Run all** for all enabled websites.
5. Use **Getting started** at any time to reopen the guide.

The recorder captures safe browser clicks and navigation keys (Enter, Tab, Escape and arrows), not arbitrary typed text, passwords, email addresses, or MFA codes. It watches the original browser tab opened by LaunchFlow and displays its connection status. The vault stays encrypted locally.

## Development

Use `BUILD-INSTALLER.ps1` on Windows with the .NET 8 SDK and Inno Setup. People installing the EXE do not need build tools. See `docs/REMAINING-GATES-0.7.34.md` for release gates and `CODE-SIGNING.md` for Authenticode signing.

No stable merge, official release or machine-policy compatibility approval is implied by a CI installer artifact.
