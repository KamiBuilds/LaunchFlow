# LaunchFlow source layout

Namespaces stay `LaunchFlow`, `LaunchFlow.Models`, `LaunchFlow.Services`, and
`LaunchFlow.Controllers`. Folders are for ownership, not API churn.

```
LaunchFlow/
  App.xaml(.cs)              entry + shared resources
  Assets/                    icons
  Domain/Models/             workflows, config drafts, mappers
  UI/
    Shell/                   MainWindow partials + XAML
    Dialogs/                 secondary windows
    Controllers/             run/teach/editor leases
  Services/
    Automation/              AutomationEngine + stages
    Login/                   recipes, field discovery, health, flow coordinator
    Teach/                   recorder + page script
    Vault/                   LFV3 SQLite vault, credentials, staged recovery
    Persistence/             config store, save transaction
    Browser/                 installs, monitors, choices
    Transfer/                import/export, legacy migration
    Workspaces/              catalog, exit gates, app control
    KeePass/                 KeePassXC bridge
    Platform/                startup, chrome, updates, AppInfo, window work-area, global shortcut
    Diagnostics/             crash + diagnostic reports
    Policy/                  EnterprisePolicy
    Vault/…PasswordSessionPolicy.cs
    Workspaces/…RunOutcomeSummary.cs
```

MainWindow remains the WPF event/lifecycle adapter. Pure wording, session
timing, and native maximize math live in the services above — not as a second
god-object.

Do not move vault codec/format, installer AppId, or workflow ID rules when
rearranging files. Historical handoff notes live in `docs/archive/`.
