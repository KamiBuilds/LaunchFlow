# MainWindow partial map

`MainWindow` remains the WPF event/lifecycle adapter. Partials are ownership
slices, not a second product. Prefer extracting **non-WPF rules** into
`Services/` or `Controllers/` when a slice causes bugs; do not blanket-split for
folder aesthetics.

| Partial | Owns |
|---|---|
| `MainWindow.xaml.cs` | Construction, tray, hotkeys, shared fields, close/exit hooks |
| `MainWindow.Execution.cs` | Run / Teach / Repair admission, Firefox readiness, taught-step publish |
| `MainWindow.VaultLocking.cs` | Lock vault, seal profiles, session close barrier |
| `MainWindow.Lifecycle.cs` | Owned exit drain, installer shutdown coordination |
| `MainWindow.Persistence.cs` | Capture/apply editor draft, `SaveAll`, durable config |
| `MainWindow.AutoSave.cs` | Debounced dirty save schedule |
| `MainWindow.Websites.cs` | Add / duplicate / delete website (draft restore on refuse) |
| `MainWindow.WebsiteEditor.cs` | Show/bind website editor controls |
| `MainWindow.Credentials.cs` | Password-manager credential save/clear |
| `MainWindow.KeePassSettings.cs` | KeePass DB/key/entry chooser + cache epochs |
| `MainWindow.Workspaces.cs` | Workspace save/preview/run (catalog + session-bound persist) |
| `MainWindow.StartupExperience.cs` | Welcome coaching, Hello UI prompts, update check |
| `MainWindow.PasswordManager.cs` | Unlock prompt, reset→setup, change master password |
| `MainWindow.Navigation.cs` | Tabs, sidebar, global shortcut capture |
| `MainWindow.Transfer.cs` | Import/export portable vault |
| `MainWindow.Steps.cs` | Workflow step list edits |
| `MainWindow.WindowsSession.cs` | OS lock / unlock reactions |
| `MainWindow.HelpAndDiagnostics.cs` | Help, diagnostics export |

## Already peeled (do not re-bury)

- `RunUiOperationController`, `TeachUiOperationController`, `WorkflowEditorSession`
- `HelloUnlockCoordinator` (enable epoch + preference persistence)
- `ConfigurationSaveTransaction`, `WorkflowConfigurationService`, `ConfigStore`
- `WorkspaceCatalog`, `ExitCompletionGate`, `WebsiteEditMapper`
- Platform helpers: `WindowWorkAreaConstraint`, `GlobalShortcutRegistrar`, …
