# Both: administrator policy and private diagnostics

## Scope and contract

This is a selective integration from `origin/smartest-app-developer` (`cecb177`) onto the repaired `hermes` foundation `c3451c2`. It does not replace the engine, recorder, KeePassXC implementation, profile vault, installer identity, encryption, schema, KDF parameters or transfer format.

The engine/UI integration contract is exactly:

```csharp
EnterprisePolicy policy = EnterprisePolicy.Current;
bool disabled = policy.DisableAutomation;
bool fillDisabled = policy.DisableCredentialFill;
bool storageDisabled = policy.DisableCredentialStorage;
bool teachDisabled = policy.DisableTeachMode;
bool transferDisabled = policy.DisableExport;
bool httpsRequired = policy.RequireHttpsForCredentials;
bool allowed = policy.IsUrlAllowed(url);
```

`Current` returns a fresh immutable policy snapshot, not a startup cache. Check again at each security-sensitive dispatch. There is no public setter, user configuration override, remote policy service, or registry-writing API. `IsUrlAllowed` checks only the URL restrictions: callers must separately enforce `DisableAutomation`, `DisableTeachMode`, and credential-specific flags/HTTPS. In a fail-closed snapshot it refuses every URL.

## Administrator authority

Windows reads the **64-bit registry view** of `HKLM\SOFTWARE\Policies\LaunchFlow`, read-only. This fixed view keeps x86/x64 application policy authority consistent. Windows native registry behavior and ACLs still require native verification; the Linux tests do not exercise Windows registry APIs.

| Value | Required registry kind | Meaning |
| --- | --- | --- |
| `DisableAutomation` | REG_DWORD, exactly 0 or 1 | Refuse engine automation when 1 |
| `DisableCredentialFill` | REG_DWORD, exactly 0 or 1 | Refuse credential retrieval/filling when 1 |
| `DisableCredentialStorage` | REG_DWORD, exactly 0 or 1 | Refuse credential creation, access, and snapshot import when 1 |
| `DisableExport` | REG_DWORD, exactly 0 or 1 | Refuse portable imports **and** exports when 1 |
| `DisableTeach` | REG_DWORD, exactly 0 or 1 | Exposed as `DisableTeachMode` |
| `RequireHttpsForCredentials` | REG_DWORD, exactly 0 or 1 | Parent must require HTTPS at the actual credential document/frame |
| `AllowedHosts` | REG_SZ | Comma/semicolon-separated exact DNS names or IP literals |

Only a successfully opened, absent Windows key is unmanaged. Read/access errors and malformed configured recognized values disable all features, require HTTPS, and refuse all URLs. Values missing from a readable managed key retain their ordinary defaults; unknown value names are ignored for forward compatibility. An empty managed key therefore imposes no restrictions. REG_SZ `"1"`, QWORD `1`, booleans, null values, and DWORD values other than 0/1 are not accepted as flags. REG_EXPAND_SZ and REG_MULTI_SZ host lists are not accepted.

Absent `AllowedHosts` means unrestricted valid HTTP(S) hosts. **Configured empty lists are invalid**, not unrestricted. Empty list entries, wildcards, URLs, paths, ports, underscores, malformed DNS labels, trailing DNS dots, scoped IPv6, and legacy shorthand/octal/integer IPv4 policy entries are rejected. DNS matching is ASCII case-insensitive, including explicitly configured punycode labels; Unicode host spellings are not automatically enrolled. IPv4 and IPv6 are compared by canonical IP representation, DNS by complete name, never substring or subdomain suffix. URL credentials/userinfo and non-HTTP(S) schemes are refused. URI parsing may canonicalize equivalent IP spellings; this is host restriction, not port/origin approval or protection against DNS rebinding.

On **non-Windows platforms**, the native provider is explicitly unsupported and returns the unmanaged defaults without attempting Registry access. This is not evidence that a Windows key is absent. `FromProvider` and scoped, async-local `UseProviderForTesting` are internal behavioral injection seams: null means proven absence, a dictionary means a readable key, and an exception means failed authority access. They are not a sandbox against hostile code already executing inside the application. No test activates machine registry policy.

Registry reads and operations are cooperative snapshots, not an atomic transaction with administrator changes. Previously dispatched actions cannot be recalled by changing a registry value.

## Production vault and transfer enforcement

Checks are inside the actual production services, not just UI switches:

- `VaultDatabase.Initialize` rejects nonempty initial credentials before creating any database under storage denial; initializing a new configuration-only vault remains possible.
- `SaveCredential`, `HasCredential`, `ReadCredential`, and `ExportCredentials` enforce credential policy even on direct database calls. Reads additionally enforce fill denial; exports additionally enforce transfer denial.
- `CredentialVault` checks before optional KeePass lookup, before locked-vault fallback results, and before export. Policy refusal throws a fixed `InvalidOperationException` mentioning the organization; it does not silently return an empty credential set.
- `ReplaceSnapshot` checks both transfer and storage policy **before** mutating the supplied workflows or database. Empty credential imports are refused under storage denial too: otherwise they would erase saved credentials.
- `SecureTransferService.Read` checks both restrictions before opening or decrypting the package. `Export` checks transfer denial for passwordless exports too, and storage denial when passwords are requested. Configuration-only export remains supported under storage denial.
- Policy denial never deletes existing credentials. Explicit user deletion remains possible. Vault lock/unlock, configuration editing and password/key maintenance retain their existing purposes. Unlock defers legacy credential-blob migration while storage is disabled. Internal resealing/password maintenance is not a new-credential enrollment path.

AES-GCM blob layout, Argon2id parameters, `launchflow.db` location/schema, workflow IDs and `LFLW1` portable format are unchanged. `PortableExport` remains the unchanged low-level codec; all current production codec calls go through `SecureTransferService`. Future callers must preserve that boundary. Policy does not detect secrets users put into arbitrary workflow/configuration text.

## Required parent integration (not owned here)

1. **AutomationEngine**: gate run/start, browser launch and each navigation/action with `DisableAutomation` and `IsUrlAllowed`; enforce actual redirect/popup/frame destinations rather than only configured start URLs. Retain the hermes document-bound credential insertion repairs. Check fill/storage/HTTPS/actual target URL immediately before credential dispatch, including async approvals and username/password continuation. Do not turn policy exceptions into an alternative credential lookup.
2. **Teach/recorder**: gate recorder creation and subsequent capture/save with `DisableTeachMode`, `DisableAutomation`, and host restrictions. Cancellation/lifetime repairs must remain intact. Recheck after awaits/policy changes.
3. **MainWindow**: disable or explain unavailable controls without rewriting saved user preferences. Guard `_vault.Read` in credential editing and `_vault.Has` in status/login-health/workflow handling: those now explicitly refuse under policy. Catch the fixed refusal at import/export/save UI boundaries. Storage denial blocks snapshot imports even if the file is configuration-only; passwordless export is allowed unless export is disabled.
4. **Credential-fill UI**: fill denial also blocks `CredentialVault.Read`, including edit-prefill. Present a fixed explanation rather than treating it as an empty existing password and saving a replacement accidentally.
5. **Tests/CI**: engine harnesses that link the engine must also link this production policy service or explicitly provide a policy seam. Run the new policy harness independently of WPF.

## Privacy behavior

`CrashLog` emits only a UTC timestamp, an explicit allowlisted category, and fixed `Event`/`Failure` codes. Unknown categories become `General`. Both existing overloads remain source-compatible, but **all message strings are discarded**, including plausible alphanumeric passwords. Exception objects are not inspected: no messages, stack traces, inner errors, custom type names, `ToString`, or arbitrary property getters. Current log and one `.previous` log are bounded to 512 KiB each for normal generated events, serialized within this process, with best-effort I/O failure handling.

`WorkflowStep.ToString()` emits only fixed action labels; values, targets/URLs/selectors, descriptions and unknown type names are never implicitly formatted. Explicit editors still have the original model fields; serialization is unchanged.

This does not sanitize existing historical log contents or unrelated UI/engine strings. Existing files may already contain sensitive data from earlier versions. Caller diagnostics-consent behavior is unchanged: some fatal/startup handlers already log without the configuration opt-in. No claim of application-wide telemetry consent/privacy is made here. Cross-process log races, hostile same-user filesystem manipulation and arbitrary untrusted code in-process are outside this logger's assurance boundary.

## Executable verification

```sh
tests/policy/run.sh
tests/policy/run-compatibility.sh
/root/.cache/launchflow/dotnet/dotnet build LaunchFlow/LaunchFlow.csproj \
  -p:EnableWindowsTargeting=true -warnaserror
```

The policy harness links actual production policy, SQLite/AES-GCM/Argon2id vault, credential wrapper, transfer codec/facade, logger, and models into a portable .NET executable. Each run creates an isolated `XDG_DATA_HOME`, asserts the production database path is inside it, uses only synthetic secrets, and removes it afterward. The optional external KeePass process alone is replaced with a no-launch sentinel. This is real SQLite/encryption evidence, not native Windows/WPF/Registry/KeePass proof.

Compatibility verification compiles the retained `c3451c2` production vault source from Git, creates a real encrypted database/package, opens and updates them using the new production vault, and reopens the updated database/package with the baseline vault. It restores the test build to current production source afterward. The portable codec is unchanged in both passes.

RED/GREEN evidence and build logs are in `/root/projects/launchflow-evidence/both/policy`. The suite covers malformed authority, exact hosts, live provider refresh, real direct vault boundaries, refusals without file mutation/data loss, transfer success/refusal, private diagnostics, rotation, and safe implicit model formatting. Initial RED logs preserve feature failures; supplemental baseline-compatibility and already-correct behaviors are not represented as new production failures.

Native Windows registry kind/view/ACL behavior, WPF usability, installer behavior, and parent engine/Teach/UI integration require separate final gates. This worker cannot independently approve its own implementation; the parent must review the frozen commit and rerun integrated gates.
