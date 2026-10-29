# LaunchFlow startup blocked by Windows Application Control (0x800711C7)

## What happened
Windows blocked the managed DLL `Microsoft.Data.Sqlite.dll`, not the stored master password. The v0.7.30 unsigned developer preview may be rejected on PCs with Smart App Control or a managed WDAC/App Control for Business policy.

The new startup preflight checks required SQLite code and opens only an **in-memory** database before showing the first-run master-password window. A failed preflight stops LaunchFlow with a clear message and cannot create or open a LaunchFlow vault. It does not repair or bypass Windows policy.

## What the tester should do
1. **Do not reset the master password or delete the vault.** If the first-run attempt already created a vault, keep the existing files; a new build can reuse it.
2. Open **Event Viewer** → **Applications and Services Logs** → **Microsoft** → **Windows** → **CodeIntegrity** → **Operational**. Look for a blocked-DLL event around the time of the failure. Also check **Microsoft-Windows-AppLocker/EXE and DLL** if the device is managed.
3. Send the administrator/developer the blocked file name, event ID and policy details, Windows edition/build and installed LaunchFlow version. **Do not send passwords, keys or entire vault files.**
4. Ask for a correctly **publisher-signed** LaunchFlow build, and where appropriate explicit approval under the organization's application-control policy. The installer EXE **and** the app's loaded DLL dependencies must be considered.
5. On a personal machine with Smart App Control, a trusted code-signed distribution may be required; simply reinstalling the same unsigned ZIP will not fix a policy block.

**Do not** disable Smart App Control, WDAC or AppLocker; rename DLLs; or relocate files to evade a policy. Release approval must come from the device owner/administrator.

## Developer checklist
- Test clean first-run and existing encrypted-vault unlock on an unrestricted Windows 10/11 VM.
- Build with a valid publisher certificate and verify the published `Microsoft.Data.Sqlite.dll`, `SQLitePCLRaw.*`, native `e_sqlite3.dll`, app executable and installer signatures.
- Test on the intended **policy-enforced** Windows machine with an approved signer policy before claiming compatibility.
- Preserve existing encrypted SQLite and profile files; never delete a vault due to dependency-load failure.
- Check policy logs for additional blocked dependencies and request legitimate policy review.
