# LaunchFlow — Getting started

Generated from the same one-action instructions as the v0.7.46 in-app guide. Control labels are verified against the English source, not native screenshots. Dutch navigation can display Instellingen instead of Settings; other screens may remain English.

## First launch (before the in-app guide)

Install the packaged app, not a developer SDK. A new installation displays Set up LaunchFlow with Master password, Confirm master password and Create vault & continue. An existing vault instead displays Unlock LaunchFlow, with Unlock and optional Windows Hello. Your vault master password is not your website password or Windows password; it cannot be recovered if forgotten. Never delete an existing vault to follow a tutorial.

After unlocking, click Getting started. This modeless guide does not click, save credentials or run automation. Next means a page was viewed, not that its action was completed.

## First website — no saved login

1. **Open Websites** (BASIC-01): Select Websites at the top of LaunchFlow.
   - **Where:** LaunchFlow → Top navigation. Look for **Websites**.
   - **You should see:** The saved website list or 'No websites yet' appears.
   - **Can't find it?** If Settings is selected, click Websites in the top navigation. Do not close your guide.
2. **Open website setup** (BASIC-02): Click + Add website.
   - **Where:** LaunchFlow → Websites · top toolbar. Look for **+ Add website**.
   - **You should see:** The 'Add a website - LaunchFlow' window opens, with 'Add your website' at the top.
   - **Can't find it?** When the list is empty, + Add your first website opens this same flow. If opening is blocked, unlock the vault or resolve unsaved edits before retrying.
3. **Name this website** (BASIC-03): Replace the Website name with Demo website.
   - **Where:** Add a website - LaunchFlow → 1. Which website?. Look for **Website name**.
   - **You should see:** Website name now shows 'Demo website'. This is just an example; use your own name.
   - **Can't find it?** The field starts with 'My website'. Click inside that box and replace its contents.
4. **Enter its address** (BASIC-04): Enter https://example.com in Website address (URL).
   - **Where:** Add a website - LaunchFlow → 1. Which website?. Look for **Website address (URL)**.
   - **You should see:** The address box shows https://example.com. This is an open-only example, not a login test account.
   - **Can't find it?** Include https:// for this example. Paste URL is an alternative; do not change advanced connection and port settings.
5. **Choose a browser** (BASIC-05): Open Browser and select an installed supported browser.
   - **Where:** Add a website - LaunchFlow → 2. Where should it open?. Look for **Browser**.
   - **You should see:** Browser shows the browser you chose.
   - **Can't find it?** Available browsers depend on this PC. Default browser can open a page but is not equivalent to a supported automation browser. Browser readiness can help if nothing compatible is installed.
6. **Choose a screen** (BASIC-06): Choose your desired display in Screen (monitor).
   - **Where:** Add a website - LaunchFlow → 2. Where should it open?. Look for **Screen (monitor)**.
   - **You should see:** Screen (monitor) shows your selected display.
   - **Can't find it?** A single-monitor PC does not need Screen 2. Monitor numbers vary by device; you can change the choice later.
7. **Keep login optional** (BASIC-07): Leave Yes, automatically sign in to this website unchecked.
   - **Where:** Add a website - LaunchFlow → 3. Automatic login (optional). Look for **Yes, automatically sign in to this website**.
   - **You should see:** The checkbox remains unchecked and no website password is required for this example.
   - **Can't find it?** For a first test, leave login off. If the checkbox was enabled, uncheck it. Website credentials belong to an optional later guide.
8. **Create the website** (BASIC-08): Click Create website.
   - **Where:** Add a website - LaunchFlow → Bottom of website setup. Look for **Create website**.
   - **You should see:** The setup window closes and the new website appears in Websites.
   - **Can't find it?** A validation warning or 'could not be saved' message means creation did not complete. Correct the entry and retry; do not delete your vault.
9. **Select the new website** (BASIC-09): Select Demo website in your website list.
   - **Where:** LaunchFlow → Websites · YOUR WEBSITES. Look for **Saved website workflows**.
   - **You should see:** The editor shows the selected website's name, address, browser and screen.
   - **Can't find it?** If no website is listed, creation may have failed. Check the previous step and its save result.
10. **Test only that website** (BASIC-10): Click Test this site.
   - **Where:** LaunchFlow → Websites · selected website editor. Look for **▶ Test this site**.
   - **You should see:** LaunchFlow attempts to open only the selected website on the selected screen.
   - **Can't find it?** Test this site saves settings first. If saving, browser readiness or validation fails, resolve that issue before another attempt; Run all is not the same action.
11. **Check the browser result** (BASIC-11): Check the browser window that LaunchFlow opened.
   - **Where:** Browser window → Selected browser and display. Look for **https://example.com**.
   - **You should see:** The intended address is shown on the intended monitor.
   - **Can't find it?** If no window opened, check the selected browser and screen and any error or sign-in notice. Avoid repeated launches while investigating.
12. **Check the save result** (BASIC-12): Read the save status in LaunchFlow.
   - **Where:** LaunchFlow → Websites · top toolbar. Look for **All changes saved**.
   - **You should see:** The status reads 'All changes saved' rather than 'Unsaved changes' or 'Save failed'.
   - **Can't find it?** At compact widths this caption can be hidden. Check the Save changes button and its tooltip; never treat a failed save as complete.
13. **Save pending changes if needed** (BASIC-13): If edits are still pending, click Save changes (or Retry save after a failed save).
   - **Where:** LaunchFlow → Websites · top toolbar. Look for **Save changes / Retry save**.
   - **You should see:** The application acknowledges a successful durable save, or shows a failure to correct.
   - **Can't find it?** Only press this if you have changes to keep. If saving fails or the vault is locked, leave edits pending and retry after resolving the cause.

## Work sites (ThousandEyes / GitHub / npm)

1. **Open Websites** (WORK-01): Select Websites at the top of LaunchFlow.
   - **Where:** LaunchFlow → Top navigation. Look for **Websites**.
   - **You should see:** Your website list is visible.
   - **Can't find it?** If Settings is selected, click Websites in the top navigation.
2. **ThousandEyes-style login** (WORK-02): Add or select your ThousandEyes site. Use Sign-in mode: saved password (Auto). Test this site once — email first, then password is automatic.
   - **Where:** LaunchFlow → Websites · selected site. Look for **▶ Test this site**.
   - **You should see:** LaunchFlow fills email, continues to password, then submits when safe.
   - **Can't find it?** If MFA appears, finish it yourself. Remembered login shape becomes 'Email first, then password' after a successful Auto run.
3. **GitHub-style login** (WORK-03): Add or select GitHub. Keep saved password (Auto). Test this site — username and password on one page.
   - **Where:** LaunchFlow → Websites · selected site. Look for **▶ Test this site**.
   - **You should see:** Both fields fill on the same form when discovery succeeds.
   - **Can't find it?** Use Fix sign-in if GitHub shows only SSO buttons or passkeys. MFA stays manual.
4. **Your own / npm-style login** (WORK-04): Add your npm or custom site. Prefer Auto first; if fields are unusual, open Advanced sign-in and set username/password CSS selectors.
   - **Where:** LaunchFlow → Websites · selected site. Look for **Advanced sign-in…**.
   - **You should see:** Custom selectors or a successful Auto run remember a CustomSelectors or SamePage shape.
   - **Can't find it?** Do not paste real production passwords into this guide. Save credentials only under Settings → Passwords.
5. **When sign-in needs you** (WORK-05): If the tray or status says MFA, SSO, or ambiguous Log In, finish that step in the browser, then use Fix sign-in only if Auto should relearn clicks.
   - **Where:** LaunchFlow → Websites · Recorded actions (optional). Look for **Fix sign-in**.
   - **You should see:** You understand why Auto paused and what to do next.
   - **Can't find it?** LaunchFlow never guesses among multiple Log In buttons and never fills OTP/MFA codes.

## Saved website login — optional

After PASS-04, Saved password continues with PASS-05–08. No login required and SSO skip password entry and use PASS-09 to save only the sign-in preference. The app's mode selection is read without accessing credentials.

1. **Open Settings** (PASS-01): Select Settings in the top navigation.
   - **Where:** LaunchFlow → Top navigation. Look for **Settings**.
   - **You should see:** Settings categories appear.
   - **Can't find it?** This route is optional. Finish testing a website without saved login first.
2. **Open website passwords** (PASS-02): Select Passwords in Settings.
   - **Where:** LaunchFlow → Settings · category list. Look for **Passwords**.
   - **You should see:** The Website passwords editor appears, or requests a master-password unlock.
   - **Can't find it?** A vault master password is separate from the website password and Windows password. Never send it to support.
3. **Choose the credential target** (PASS-03): Choose the intended site in Website account to update.
   - **Where:** LaunchFlow → Settings → Passwords. Look for **Website account to update**.
   - **You should see:** The chosen website appears in the password account dropdown.
   - **Can't find it?** Check the selected account carefully, even if you selected another website in Websites. Unsaved password edits may require confirmation before switching.
4. **Choose one sign-in mode** (PASS-04): Choose your preferred option under How should LaunchFlow sign in?
   - **Where:** LaunchFlow → Settings → Passwords. Look for **How should LaunchFlow sign in?**.
   - **You should see:** One mode is selected: saved password, no login or browser SSO. The guide will follow that choice.
   - **Can't find it?** Saved password has separate credential-entry steps. No login and SSO only need settings saved; SSO/MFA may require manual attention. Never authorize untrusted login origins.
5. **Enter an account name** (PASS-05): If updating it, enter a username in New username or email (leave blank to keep current).
   - **Where:** LaunchFlow → Settings → Passwords. Look for **New username or email (leave blank to keep current)**.
   - **You should see:** Only the newly entered value is visible; a previously saved value is not prefilled.
   - **Can't find it?** Blank means keep the existing username. Never record or copy real credentials into this guide.
6. **Enter a website password** (PASS-06): If updating it, enter the website password in New password (leave blank to keep current).
   - **Where:** LaunchFlow → Settings → Passwords. Look for **New password (leave blank to keep current)**.
   - **You should see:** The password stays masked in the application field.
   - **Can't find it?** Blank means keep a saved password, if one exists. LaunchFlow's vault password is not the password for this website.
7. **Save to the chosen website** (PASS-07): Click Save for selected website.
   - **Where:** LaunchFlow → Settings → Passwords. Look for **Save for selected website**.
   - **You should see:** LaunchFlow either confirms encrypted credential saving or displays a failure.
   - **Can't find it?** Confirm the website picker and unlock state first. Saving here does not change your password at the website itself.
8. **Read the credential status** (PASS-08): Check the credential status for your chosen website.
   - **Where:** LaunchFlow → Settings → Passwords. Look for **Encrypted credentials saved**.
   - **You should see:** The status confirms that the selected website's credentials were saved.
   - **Can't find it?** If the status still says 'No password saved' or an error appears, do not assume storage succeeded. A blocked IdP/MFA step needs manual attention.
9. **Save No login or SSO settings** (PASS-09): Click Save changes (or Retry save if a previous save failed).
   - **Where:** LaunchFlow → Top toolbar · website settings. Look for **Save changes / Retry save**.
   - **You should see:** LaunchFlow confirms that the selected sign-in preference was saved, or shows a failure to fix.
   - **Can't find it?** Do not enter a new password or click Save for selected website for No login/SSO. A mode change does not remove stored credentials. Confirm the save status and retry after resolving failures.

## Record clicks — optional

1. **Choose the website** (RECORD-01): Select the intended website from Websites.
   - **Where:** LaunchFlow → Websites · YOUR WEBSITES. Look for **Saved website workflows**.
   - **You should see:** The correct site's details are visible.
   - **Can't find it?** Choose an owned, low-risk test page. Do not record real password or MFA entry.
2. **Start the recorder** (RECORD-02): Click Record clicks.
   - **Where:** LaunchFlow → Websites · Recorded actions (optional). Look for **● Record clicks**.
   - **You should see:** The 'Record actions - LaunchFlow' window opens.
   - **Can't find it?** This is a separate recorder window. It must attach to the page opened by LaunchFlow.
3. **Check recorder readiness** (RECORD-03): Read the capture and target status.
   - **Where:** Record actions - LaunchFlow → Recorder · status. Look for **CaptureStatus / TargetStatus**.
   - **You should see:** The recorder identifies the intended browser tab instead of staying on Connecting.
   - **Can't find it?** Wait for readiness before clicking. If it cannot attach, stop and investigate rather than recording another personal browser tab.
4. **Make one harmless click** (RECORD-04): Click one non-sensitive button in the original LaunchFlow browser tab.
   - **Where:** Browser window → Original recording tab. Look for **A safe test button**.
   - **You should see:** A corresponding action appears in Recorded actions.
   - **Can't find it?** Do not enter passwords, account numbers, email addresses, OTPs or private text. Unsupported popups and frames may need manual setup.
5. **Review the actions** (RECORD-05): Review the Recorded actions list before saving.
   - **Where:** Record actions - LaunchFlow → Recorder · recorded list. Look for **Recorded actions**.
   - **You should see:** Only intended navigation and clicks are listed.
   - **Can't find it?** Remove accidental actions using the recorder controls. Secret-detection cannot be guaranteed for every custom web widget.
6. **Save reviewed actions** (RECORD-06): Click Save actions.
   - **Where:** Record actions - LaunchFlow → Recorder · bottom actions. Look for **Save actions**.
   - **You should see:** The actions are offered to the selected website's workflow.
   - **Can't find it?** If the vault locks or the website was replaced, LaunchFlow must refuse stale recording ownership. Cancel does not mean Save.

## Startup and background — optional

1. **Open Settings** (STARTUP-01): Select Settings in the top navigation.
   - **Where:** LaunchFlow → Top navigation. Look for **Settings**.
   - **You should see:** Settings categories appear.
   - **Can't find it?** This optional route does not enable automation by itself.
2. **Open Startup** (STARTUP-02): Select Startup in Settings.
   - **Where:** LaunchFlow → Settings · category list. Look for **Startup**.
   - **You should see:** Background and trigger options are visible.
   - **Can't find it?** These settings can be restricted by company policy. Do not enable them on a managed PC without approval.
3. **Review Windows startup** (STARTUP-03): Review Start hidden with Windows (enables Ctrl+M).
   - **Where:** LaunchFlow → Settings → Startup. Look for **Start hidden with Windows (enables Ctrl+M)**.
   - **You should see:** The checkbox reflects the current preference.
   - **Can't find it?** Turning this on requests startup registration; a checked setting alone does not prove Windows allowed it.
4. **Review unlock automation** (STARTUP-04): Review Run enabled websites after Windows unlock.
   - **Where:** LaunchFlow → Settings → Startup. Look for **Run enabled websites after Windows unlock**.
   - **You should see:** The checkbox reflects whether an unlock-triggered run is requested.
   - **Can't find it?** Vault lock and Windows lock are different. Credentials may require unlocking before automation resumes.
5. **Review background mode** (STARTUP-05): Review Allow LaunchFlow to run in the background (system tray).
   - **Where:** LaunchFlow → Settings → Startup. Look for **Allow LaunchFlow to run in the background (system tray)**.
   - **You should see:** The background preference is visible.
   - **Can't find it?** Minimize, close-to-tray and Exit are different. Closing the window can show an Exit/Background choice.

## Troubleshooting

1. **A browser is unavailable** (HELP-01): Open Browser readiness and select Check again after installing an approved browser.
   - **Where:** Browser readiness → Browser readiness dialog. Look for **Check again**.
   - **You should see:** The dialog updates its detected browser information.
   - **Can't find it?** The dialog does not silently install browsers. Use official downloads only when authorized by your device's software policy.
2. **The wrong website is selected** (HELP-02): Select the correct website in YOUR WEBSITES.
   - **Where:** LaunchFlow → Websites · YOUR WEBSITES. Look for **Saved website workflows**.
   - **You should see:** The selected website editor shows the intended name and address.
   - **Can't find it?** Do not select away from an unsaved editor without handling its warning. In Passwords, independently confirm Website account to update.
3. **Automatic sign-in needs attention** (HELP-03): Choose Fix sign-in on the selected website.
   - **Where:** LaunchFlow → Websites · Recorded actions (optional). Look for **Fix sign-in**.
   - **You should see:** LaunchFlow shows a repair or manual-attention path.
   - **Can't find it?** Use human sign-in for MFA/SSO. Do not disable HTTPS checks or guess untrusted login buttons.
4. **Saving failed** (HELP-04): After fixing the error, click Retry save (or Save changes if that is the current label).
   - **Where:** LaunchFlow → Websites · top toolbar. Look for **Save changes / Retry save**.
   - **You should see:** A successful durable save is acknowledged.
   - **Can't find it?** Keep pending edits intact. If the vault is locked, unlock it before retrying. A failed write is not a completed save.
5. **Browser cleanup failed** (HELP-05): After resolving the affected browser session, choose Retry vault lock.
   - **Where:** LaunchFlow → Websites · top toolbar. Look for **Retry vault lock**.
   - **You should see:** The application retries a controlled vault lock and browser cleanup.
   - **Can't find it?** Do not force-delete profiles or assume browser sessions are closed if cleanup failed. This button appears only in the failure state.
6. **Vault file looks broken** (HELP-06): If LaunchFlow says the vault is incomplete, choose Restore from a recovery copy or a .launchflow backup. Do not delete launchflow.db yourself.
   - **Where:** LaunchFlow → Startup vault recovery. Look for **Vault recovery required**.
   - **You should see:** LaunchFlow either restores a SQLite recovery copy, imports a portable backup after quarantining the broken file, or exits with the folder open.
   - **Can't find it?** Recovery copies live under %LOCALAPPDATA%\LaunchFlow\recovery. Prefer restore over Reset. Reset permanently removes local secrets.

## Confirm a website survives close and reopen

Once LaunchFlow has confirmed a durable save, use its normal Exit path (not simply minimize/Background). Then reopen LaunchFlow, unlock the same vault, and confirm the website name, URL, browser and screen remain. Do this manually; the guide does not exit/restart the app. If Exit reports unsaved edits, a locked vault or failed cleanup, follow that recovery path instead of forcing shutdown.

## Visual provenance and limitations

The in-app locator is a source-based schematic, not a native screenshot. Show target in app / Open this area may safely open a matching clean tab, then outline a visible control without clicking or changing it. A confirmation checkbox records only the user's own in-memory observation, not evidence of completed storage or login. Native Windows captures, layout, keyboard, language, DPI and multi-monitor behavior require separate acceptance tests.
