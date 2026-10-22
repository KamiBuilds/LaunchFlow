# LaunchFlow v0.7.8 — UI cleanup and browser recovery

## Included changes

1. **Cleaner top bar**
   - Removed the extra large header logo so the title area is less cluttered.

2. **Browser profile recovery**
   - If Edge/Chrome says the workflow profile is already in use, LaunchFlow now:
     - retries after deleting lock files
     - falls back to a recovery profile instead of immediately failing
   - This should reduce the raw "Opening in existing browser session" failure.

3. **Cleaner Settings layout**
   - The Settings area now uses a left-side category layout, closer to Visual Studio Code.

4. **Friendlier browser messages**
   - Browser-related popups now use clearer wording instead of raw Chromium text where possible.

## Notes

- If a recovery profile is used, the website may ask you to sign in again once.
- Existing encrypted vault data and installed app identity stay compatible with earlier versions.
