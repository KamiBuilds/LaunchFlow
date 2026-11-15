# AutomationEngine partial map

`AutomationEngine` is intentionally a **cohesive automation subsystem**, not a
thin one-file runner. Login, browser session, steps and confirmation share one
gate, one session table and one cancellation owner. Splitting into independent
services without shared `_gate` ownership recreates races.

| Partial | Owns |
|---|---|
| `AutomationEngine.cs` | Session table, run/close lifecycle, public Run API, disposal |
| `AutomationEngine.Browser.cs` | Persistent context launch, profile dir, valuable-seal guard |
| `AutomationEngine.Steps.cs` | Recorded Navigate/Click/Fill/Press with live policy/frame checks |
| `AutomationEngine.Login.cs` | Top-level login orchestration entry |
| `AutomationEngine.LoginStages.cs` | Identifier-first / SamePage hint vs current form |
| `AutomationEngine.StagedLogin.cs` | Multi-stage advance and password completion |
| `AutomationEngine.AutoDiscovery.cs` | Conservative field/button discovery |
| `AutomationEngine.Confirmation.cs` | Manual sign-in acknowledgment + structural challenge |

## Related owners outside the engine

| Type | Role |
|---|---|
| `LoginHealth` / login recipes | Evidence and remembered hints (never override live form authority) |
| `LoginFlowCoordinator` | UI-facing login mode resolution helpers |
| `TeachRecorder` | Capture only; replay goes through `Steps` |
| `ProfileVaultService` | Seal/restore; engine refuses valuable empty placeholders |
| `EnterprisePolicy` | Live automation/host/fill gates |

## Extraction rule

Only peel a partial into a new type when characterization tests show the shared
session gate is the bug, or the new type can take an explicit session handle.
Do not introduce a second Playwright owner beside `AutomationEngine`.
