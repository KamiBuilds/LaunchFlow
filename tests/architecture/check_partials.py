#!/usr/bin/env python3
"""Refactor invariants: module ownership and source-linked test coverage.

Intentionally validates structural boundaries without loading real vaults,
browser profiles, user settings, registry keys or machine credentials.
"""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
UI = ROOT / "LaunchFlow"
ENGINE = UI / "Services"
ui_sources = {p.name: p.read_text(encoding="utf-8") for p in UI.glob("MainWindow*.cs")}
engine_sources = {p.name: p.read_text(encoding="utf-8") for p in ENGINE.glob("AutomationEngine*.cs")}
required_ui = {
    "MainWindow.xaml.cs", "MainWindow.Websites.cs", "MainWindow.Persistence.cs",
    "MainWindow.Execution.cs", "MainWindow.Credentials.cs",
    "MainWindow.WebsiteEditor.cs", "MainWindow.VaultLocking.cs",
    "MainWindow.WindowsSession.cs", "MainWindow.KeePassSettings.cs",
    "MainWindow.Navigation.cs", "MainWindow.Steps.cs", "MainWindow.Transfer.cs",
    "MainWindow.PasswordManager.cs", "MainWindow.StartupExperience.cs",
}
required_engine = {
    "AutomationEngine.cs", "AutomationEngine.Browser.cs",
    "AutomationEngine.Login.cs", "AutomationEngine.Steps.cs",
    "AutomationEngine.Confirmation.cs",
}
assert required_ui.issubset(ui_sources), f"Missing UI modules: {sorted(required_ui - ui_sources.keys())}"
assert required_engine.issubset(engine_sources), f"Missing engine modules: {sorted(required_engine - engine_sources.keys())}"

assert len(ui_sources["MainWindow.xaml.cs"].splitlines()) <= 950, "UI shell grew beyond the refactor budget"
assert len(engine_sources["AutomationEngine.cs"].splitlines()) <= 450, "Engine lifecycle core grew beyond its budget"

# Every moved entry point has exactly one owner. These are the key methods that
# previously caused navigation, saving, credential or browser lifecycle coupling.
owners = {
    "MainWindow.Websites.cs": ["AddWorkflow_Click", "DeleteWorkflow_Click", "WorkflowList_SelectionChanged"],
    "MainWindow.Persistence.cs": ["SaveAll", "ApplyCurrentEditor"],
    "MainWindow.Execution.cs": ["RunEnabledAsync", "SaveTaughtStepsAsync", "RepairLoginCoreAsync"],
    "MainWindow.Credentials.cs": ["SaveCredentials_Click"],
    "MainWindow.PasswordManager.cs": ["RequireMasterPassword", "ChangeMasterPassword_Click"],
    "MainWindow.VaultLocking.cs": ["SecureVaultLockCoreAsync"],
    "MainWindow.WindowsSession.cs": ["OnWindowsLocked", "OnWindowsUnlockedAsync"],
    "MainWindow.Navigation.cs": ["MainTabs_SelectionChanged", "SettingsTabs_SelectionChanged"],
    "MainWindow.Transfer.cs": ["ImportLaunchFlowFile"],
    "AutomationEngine.Login.cs": ["RunOneAsync", "TryAutoLoginAsync"],
    "AutomationEngine.Steps.cs": ["ExecuteStepAsync", "ExecuteElementActionAsync"],
    "AutomationEngine.Confirmation.cs": ["ConfirmSignedInAsync"],
}
for owner, names in owners.items():
    collection = ui_sources if owner.startswith("MainWindow") else engine_sources
    for name in names:
        pattern = re.compile(r"^\s{4}(?:private|public|internal|protected)\s+[^()\n]*?\b" + re.escape(name) + r"\s*\(", re.M)
        found = [file for file, source in collection.items() if pattern.search(source)]
        assert found == [owner], f"{name} expected only in {owner}, found {found}"

# Keep the test harness linked to production partial implementations, rather
# than silently passing tests against copies of old methods.
extractor = (ROOT / "tests/workspaces/extract-ui-methods.py").read_text(encoding="utf-8")
ui_check = (ROOT / "tests/ui/verify_ui.py").read_text(encoding="utf-8")
assert 'glob("MainWindow*.cs")' in extractor
assert 'glob("MainWindow*.cs")' in ui_check
assert 'glob("AutomationEngine*.cs")' in ui_check

# Keep extracted state ownership real: old fields must not silently reappear
# in a partial class alongside the new controller. Structural checks cannot
# replace the executable dispatcher/browser tests linked by the harness.
controllers = UI / "Controllers"
run_owner = (controllers / "RunUiOperationController.cs").read_text(encoding="utf-8")
editor_owner = (controllers / "WorkflowEditorSession.cs").read_text(encoding="utf-8")
mapper = (UI / "Models" / "WebsiteEditMapper.cs").read_text(encoding="utf-8")
login_coordinator = (ENGINE / "LoginFlowCoordinator.cs").read_text(encoding="utf-8")
login_observer = (ENGINE / "LoginCompletionObserver.cs").read_text(encoding="utf-8")
login_stages = (ENGINE / "AutomationEngine.LoginStages.cs").read_text(encoding="utf-8")
combined_ui = "\n".join(ui_sources.values())
for legacy in ("_runInProgress", "_runStatusActive", "_runCancellation",
               "_activeRunTask", "_selected", "_editGeneration", "_hasUnsavedChanges"):
    assert not re.search(r"\b" + re.escape(legacy) + r"\b", combined_ui), (
        f"Legacy MainWindow mutable owner returned: {legacy}")
for boundary in ("TryBeginRun", "AttachCancellation", "TrackTask",
                 "IsStatusCurrent", "Complete"):
    assert boundary in run_owner, f"Missing UI operation lease boundary: {boundary}"
assert "CaptureOwnership" in editor_owner and "_selectionEpoch" in editor_owner
assert "_editorSession.IsCurrent(editorOwner)" in ui_sources["MainWindow.Persistence.cs"]
for field in ("Enabled", "Name", "StartUrl", "DestinationUrl", "Browser",
              "MonitorIndex", "MonitorDeviceName", "LoginMode", "AutoLogin",
              "UsernameContinueSelector", "PasswordCredentialOrigin",
              "KeepassEntryPath", "UsernameSelector", "PasswordSelector",
              "SubmitSelector", "CredentialOrigin", "LoginSuccessSelector",
              "LoginSuccessUrlContains", "LoginWaitSeconds"):
    assigns = re.findall(r"\bworkflow\." + field + r"\s*=", mapper)
    assert len(assigns) == 1, f"Mapper must assign {field} exactly once"
assert "WebsiteEditMapper.Apply" in ui_sources["MainWindow.Persistence.cs"]
assert "LoginFlowCoordinator.RunAsync" in engine_sources["AutomationEngine.Login.cs"]
assert "LoginCompletionObserver." in login_stages and "FillCredentialAsync" in login_stages
for name, contents in (("RunUiOperationController", run_owner),
                       ("WorkflowEditorSession", editor_owner)):
    for forbidden in ("using System.Windows", "Microsoft.Playwright", "VaultDatabase",
                      "Microsoft.Data.Sqlite", "System.Windows.Forms"):
        assert forbidden not in contents, f"{name} became platform/storage-dependent"
for project, required in (
    ("tests/workspaces/WorkspaceTests.csproj", "Controllers/*.cs"),
    ("tests/autosave/AutosaveIntegrationTests.csproj", "Controllers/*.cs"),
    ("tests/runtime/RuntimeTests.csproj", "LoginFlowCoordinator.cs"),
    ("tests/recorder/RecorderTests.csproj", "LoginCompletionObserver.cs")
):
    assert required in (ROOT / project).read_text(encoding="utf-8"), (
        f"Missing production collaborator compile link in {project}")

print(f"PASS architecture: {len(ui_sources)} UI modules, {len(engine_sources)} engine modules")
print(f"PASS root sizes: MainWindow {len(ui_sources['MainWindow.xaml.cs'].splitlines())} lines, "
      f"AutomationEngine {len(engine_sources['AutomationEngine.cs'].splitlines())} lines")
print("PASS moved method ownership and production-linked test seams")
