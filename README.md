# LaunchFlow

**Open your websites. Sign in when needed. Replay the clicks you taught it.**

LaunchFlow is a small Windows app for people who open the same sites every day (work dashboards, shops, tools). It keeps your website passwords in an encrypted vault on *your* PC, opens each site on the screen you choose, and can replay safe clicks you recorded earlier.

> Current build: **v0.8.3** (test build — not a signed public release yet).

---

## Sharing LaunchFlow with another person — NO shared master password

Do **not** ZIP your entire Windows user profile or `%LOCALAPPDATA%\\LaunchFlow`.
That directory contains your personal encrypted vault (`launchflow.db`), which
is tied to *your* chosen master password and never belongs in an installer or
source ZIP. Anyone receiving a clean install must create their **own** master
password and their **own empty vault** on first launch.

- To share the app with a normal user, send the versioned
  `LaunchFlow-Setup-v0.8.3-x64.exe` built by `BUILD-INSTALLER.ps1`.
- To share all *source files*, run
  `./MAKE-SAFE-ZIP.ps1 -Kind Source` rather than manually compressing your
  working folder. This takes Git-tracked files only and refuses vault data.
- To share the self-contained executable folder as a ZIP, run
  `./MAKE-SAFE-ZIP.ps1 -Kind Portable` after `BUILD-INSTALLER.ps1`.
  This validates the published files before archiving them.

**Important:** Your existing vault is never automatically copied or reset.
The setup does not contain a master password. If a recipient sees your old
vault, the files they received were not a clean distribution; stop sharing
that archive until its contents have been inspected.

## What you get as a normal user

You only need the **installer EXE** (or the installed app shortcut).

You do **not** need:

- PowerShell (`.ps1`) scripts  
- Git, GitHub, Python, or the .NET SDK  
- Any of the folders in this source zip (`tests`, `tools`, `scripts`, …)

Those files are for people who **build** LaunchFlow. End users install once and use the app like any other Windows program.

---

## Install (end user)

1. Run the LaunchFlow installer (`.exe`) from a trusted source.
2. Finish setup. Windows may ask permission to install.
3. Open **LaunchFlow** from the Start menu.
4. Create a **master password** (first run) or unlock your existing vault.

Your master password unlocks the vault. It is **not** your website password and **not** your Windows password. If you forget it, the vault cannot be recovered.

---

## First 5 minutes

1. Click **Add website**.
2. Type the site address (example: `app.thousandeyes.com` or `github.com`).
3. Choose browser and screen.
4. Pick how sign-in should work:
   - **No login required** — just open the page  
   - **Saved password (Auto)** — LaunchFlow fills the login you save under **Settings → Passwords**  
   - **Browser SSO** — you finish sign-in yourself (Google / Microsoft / company SSO)
5. Click **Test this site**. Watch the browser. Finish MFA yourself if asked.
6. Optional: **Record clicks** for buttons you always press after login (not passwords).
7. Use **Run all** when you want every enabled site opened together.

Open **Getting started** anytime for short, one-step tips.

---

## Simple examples

| You want… | Do this |
|-----------|---------|
| Just open a site every morning | Add website → **No login required** → Test → enable it → Run all |
| ThousandEyes / email-then-password | Add site → **Saved password (Auto)** → save email + password under Passwords → Test |
| GitHub-style (user + password together) | Same as above; Auto detects one-page login |
| Company Google / Microsoft login | Prefer **Browser SSO**, or approve the login page when asked |
| Click “Reports” after login | Test login once, then **Record clicks** on that button |

---

## Passwords & vault (plain language)

- Passwords are stored **encrypted on this computer**, not in the cloud.
- A **new user** starts with an **empty vault** — no sample websites, no test accounts.
- LaunchFlow never needs your real vault during automated tests; tests use fake data.
- Lock the vault when you leave the desk (or let the idle lock do it).

---

## Recorder — what it does and does not do

**Does:** remember safe clicks and simple keys (Enter, Tab, arrows) on the tab LaunchFlow opened.  
**Does:** when you move to another website in that same tab (for example Google → a shop), it can save a **Navigate** step to that new address (without query secrets), then keep recording clicks there.

**Does not:** type passwords, emails, or MFA codes into the recording.  
**Does not:** magically understand “pick Samsung 512 GB” as meaning — it records the **click targets** you hit, in order. If the shop redesigns its buttons, re-record those steps.

So: Coolblue phone → brand → storage can work **if those are normal clicks** on stable buttons. It can break when the site changes layout, uses heavy pop-ups, or needs typing into search boxes.

---

## Email-then-password on another website (ThousandEyes / Cisco / Okta / …)

Many work sites ask for your **email first**, then send you to another host for the **password** (for example Cisco Webex Identity Broker, Okta, or Microsoft).

1. Use **Saved password (Auto)** and save email + password under **Settings → Passwords**.
2. On first run, LaunchFlow may ask: “Allow password on this other website?” — only click **Yes** if you recognize your company’s real sign-in page.
3. That choice is saved as **Trusted password page** (advanced website settings) so the next run does not ask again.

This is **not** hardcoded to ThousandEyes. Any site with the same pattern works the same way.  
If the second step is pure browser SSO with no password field you control, pick **Browser SSO** instead.

**Browsers:** LaunchFlow **finds** Edge and Chrome on this PC and Auto uses those installs. **Firefox Auto** uses the Playwright Firefox build shipped in Setup (mozilla.org Firefox is detected but cannot be driven). Open **Browser readiness → Scan again** to refresh detection.

**ThousandEyes notes (from Cisco docs):** enter email → optional region (Default/US1/US2/EU) → Log In. Local password may stay on `app.thousandeyes.com/v2/login/password`, or Cisco Account / org SSO may send you to `idbroker*.webex.com` / another IdP — approve that host once when LaunchFlow asks. Allow cookies for `thousandeyes.com`, `webex.com`, and `cisco.com`.

---

## For builders (developers only)

The Windows builder does **not** need a separately installed Firefox, Edge, or Chrome. It downloads the isolated Playwright Firefox automation runtime, then uses short temporary paths during Inno Setup compilation. A network connection is needed to download the Playwright runtime when building; normal users do not need to run this builder.

Source preview, not what customers install:

```text
BUILD-INSTALLER.ps1          → Windows installer (needs .NET 8 SDK + Inno Setup)
tests/                       → automated checks
tools/, scripts/             → signing and packaging helpers
```

See `docs/NATIVE-WINDOWS-ACCEPTANCE.md` before calling anything a release.

---

## Privacy & honesty

- Local vault, local browsers.
- No “fully secure / production-ready” claim without a native Windows acceptance pass and signing.
- MFA, captchas, and bank-style confirmations stay **manual**.
