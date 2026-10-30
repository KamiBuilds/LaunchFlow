#!/usr/bin/env python3
"""Validate the WPF navigation, workspace selector, dialog markup, and release version."""
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[2]
xns = "{http://schemas.microsoft.com/winfx/2006/xaml}"
errors = []

def check(condition, description):
    if not condition:
        errors.append(description)
    print(("PASS " if condition else "FAIL ") + description)

views = list((root / "LaunchFlow").glob("*.xaml"))
for view in views:
    ET.parse(view)
check(len(views) >= 10, "WPF views are well-formed XML")

ui = ET.parse(root / "LaunchFlow/MainWindow.xaml").getroot()
app = ET.parse(root / "LaunchFlow/App.xaml").getroot()
named = {element.get(xns + "Name"): element for element in ui.iter()
         if element.get(xns + "Name")}
required = {"MainTabs", "WorkflowsNav", "SettingsNav", "SettingsTabs",
            "WorkflowList", "WorkflowEditorScroll", "WorkspaceCombo",
            "WorkspaceNameBox", "SaveWorkspaceButton", "RunWorkspaceButton",
            "WorkflowStatusLabel", "HeaderVersionLabel", "CurrentVersionLabel"}
check(required.issubset(named), "website navigation and legacy workspace compatibility controls retained")
legacy = named.get("LegacyWorkspacePanel")
check(legacy is not None and legacy.get("Visibility") == "Collapsed" and
      legacy.get("IsHitTestVisible") == "False" and
      legacy.get("Focusable") == "False" and
      {child.get(xns + "Name") for child in legacy.iter() if child.get(xns + "Name")} >=
      {"WorkspaceCombo", "WorkspaceNameBox", "SaveWorkspaceButton",
       "PreviewWorkspaceButton", "RunWorkspaceButton"},
      "workspace profile controls are inaccessible and hidden while legacy profile data remains usable")
markup = ET.tostring(ui, encoding="unicode")
check('Text="YOUR WEBSITES"' in markup and
      'Content="+ Add website"' in markup and
      'Content="▶ Run all"' in markup and
      'Text="New / existing name"' not in markup and
      'Text="Saved workspace"' not in markup and
      'Text="Everything ready for your next shift."' not in markup,
      "website list and Run all form the only visible workflow-management experience")
selector = named.get("WorkspaceCombo")
check(selector is not None and selector.get("IsEditable") == "False"
      and selector.get("DisplayMemberPath") == "Name"
      and selector.get("SelectionChanged") == "WorkspaceCombo_SelectionChanged",
      "saved workspaces use selectable non-editable dropdown")
check(selector is not None and named["WorkspaceNameBox"] is not selector,
      "workspace creation uses a separate text field")
check(named["WorkspaceNameBox"].get("TextChanged") == "WorkspaceNameBox_TextChanged" and
      "PreviewWorkspaceButton" in named,
      "typing another workspace name revokes the previous selection")
source = (root / "LaunchFlow/MainWindow.xaml.cs").read_text(encoding="utf-8")
check(all(handler in source for handler in
          ("WorkspaceCombo_SelectionChanged", "SaveWorkspace_Click",
           "PreviewWorkspace_Click", "RunWorkspace_Click")),
      "workspace selection, save, preview and run handlers present")
check('WorkspaceCombo.Text.Trim()' not in source,
      "workspace execution no longer relies on ComboBox text")
check('WorkspaceCatalog.Save(_configuration, name' in source and
      'WorkspaceCatalog.IsCurrentSelection' in source and
      'PreviewWorkspaceButton.IsEnabled = available && savedWorkspaceSelected' in source,
      "workspace save, preview and run share rollback-safe validated selection")
check('WorkspaceCatalog.ConfirmationStillValid(' in source and
      '_workspaceRevision++' in source and
      'VaultDatabase.Current.ReplaceSnapshot(projected, imported.Credentials);' in source and
      'ImportConfigurationProjection.ForThisDevice(' in source and
      '_workspaceRevision++;' in source,
      "encrypted import invalidates modal workspace overwrite approval")
check('WorkspaceCatalog.PendingRunIsCurrent(' in source and
      'launchRevision = _workspaceRevision' in source and
      'WorkspaceCombo.SelectedItem, workspace' in source and
      'SaveDurable(_configuration)' in source,
      "modal workflow dispatch rechecks current workspace and saves are durable")
store = (root / "LaunchFlow/Services/ConfigStore.cs").read_text(encoding="utf-8")
check('public void SaveDurable(Configuration configuration)' in store and
      'VaultDatabase.Current.SaveConfiguration(configuration)' in store,
      "durable workspace persistence never falls back to transient storage")
extractor = (root / "tests/workspaces/extract-ui-methods.py").read_text(encoding="utf-8")
check(extractor.count('read_text(encoding="utf-8")') >= 2 and
      'write_text(output, encoding="utf-8")' in extractor,
      "Windows UI harness reads and writes explicit UTF-8")
wizard = ET.parse(root / "LaunchFlow/NewWorkflowWindow.xaml").getroot()
wizard_named = {el.get(xns + "Name"): el for el in wizard.iter() if el.get(xns + "Name")}
check(wizard_named["AutoLoginCheck"].get("IsChecked") == "False" and
      "No login required" in ET.tostring(wizard, encoding="unicode") and
      'LoginMode = autoLogin ? "Auto" : "None"' in
      (root / "LaunchFlow/NewWorkflowWindow.xaml.cs").read_text(encoding="utf-8"),
      "new local websites default to explicit No login required without credential prompts")
check('Content="No login required' in ET.tostring(ui, encoding="unicode") and
      'WorkflowsNav.Content = Text("Websites");' in source and
      'SaveWorkspaceButton.Content = Text("Save");' in source and
      'PreviewWorkspaceButton.Content = Text("Preview");' in source,
      "existing website login modes are clear and workspace actions avoid clipping")
app_source = (root / "LaunchFlow/App.xaml.cs").read_text(encoding="utf-8")
check('EXIT_FOR_INSTALL' in app_source and 'RequestShutdownForInstallAsync()' in app_source and
      '--exit-for-install' in app_source and
      'internal async Task RequestShutdownForInstallAsync()' in source,
      "installer requests coordinated app shutdown through single-instance pipe")
iss_source = (root / "installer/LaunchFlow.iss").read_text(encoding="utf-8")
check('function InitializeSetup(): Boolean;' in iss_source and
      "CheckForMutexes('LaunchFlow.Windows.SingleInstance')" in iss_source and
      "'--exit-for-install'" in iss_source and 'Sleep(250)' in iss_source,
      "installer waits for graceful exit and retains mutex protection")
style = (root / "LaunchFlow/App.xaml").read_text(encoding="utf-8")
check('SelectionBoxItemTemplate' in style and 'PART_Popup' in style
      and 'ItemsPresenter' in style,
      "dropdown renders display-member templates and accessible popup items")
check('BrowserChoices.Refresh(BrowserCombo, w.Browser)' in source and
      'BrowserCombo_DropDownOpened' in ET.tostring(ui, encoding="unicode") and
      'BrowserInstallations.IsInstalled(browser)' in (root / "LaunchFlow/NewWorkflowWindow.xaml.cs").read_text(encoding="utf-8"),
      "both browser selectors use installation detection and retain configured browser identity")
check('ObserveLoginCompletionAsync' in (root / "LaunchFlow/Services/AutomationEngine.cs").read_text(encoding="utf-8") and
      'VerifiedRedirectAsync' in (root / "LaunchFlow/Services/LoginHealth.cs").read_text(encoding="utf-8") and
      'var wasLoading = _loadingEditor;' in source and
      'CloseSessionsRetry' in source,
      "sign-in verification and safe installer cleanup recovery remain wired")
check('SignInDecisionWindow.Decision.Confirmed' in source and
      (root / "LaunchFlow/SignInDecisionWindow.xaml").is_file() and
      'ConfirmSignedInAsync' in (root / "LaunchFlow/Services/AutomationEngine.cs").read_text(encoding="utf-8") and
      'session._pendingActions.Enqueue(("Navigate", safeUrl))' in (root / "LaunchFlow/Services/TeachRecorder.cs").read_text(encoding="utf-8"),
      "human sign-in confirmation and automatic cross-site Teach navigation are connected")
check('dialog.Show();' in source and
      'ApplyModelessSignInDecisionAsync' in source and
      'decision.ShowDialog();' not in source and
      'new AboutWindow { Owner = this }.Show();' in source and
      'new BrowserSetupWindow { Owner = this }.Show();' in source,
      "noncritical windows remain modeless while dedicated authorization prompts stay gated")
check('CredentialDeleteGuard.IsCurrent(' in source and
      'ImportConfigurationProjection.ForThisDevice(' in source and
      'VaultDatabase.Current.ReplaceSnapshot(projected, imported.Credentials)' in source and
      '_configuration = projected;' in source and
      'SealProfilesCheck.IsChecked = _configuration.SealBrowserProfilesOnLock;' in source and
      'KeePassDbBox.Text = _configuration.KeePassDatabasePath ?? "";' in source,
      "safe deletion and full imported configuration projection remain wired to storage and UI")
check('WindowsApplicationControl.CheckRequiredComponents()' in (root / "LaunchFlow/App.xaml.cs").read_text(encoding="utf-8") and
      'Microsoft.Data.Sqlite' in (root / "LaunchFlow/Services/WindowsApplicationControl.cs").read_text(encoding="utf-8"),
      "startup checks blocked DLLs before new vault setup")
wizard_xml = (root / "LaunchFlow/NewWorkflowWindow.xaml").read_text(encoding="utf-8")
main_xml = (root / "LaunchFlow/MainWindow.xaml").read_text(encoding="utf-8")
tutorial_code = (root / "LaunchFlow/TutorialWindow.xaml.cs").read_text(encoding="utf-8")
check('START HERE · 3 EASY STEPS' in main_xml and
      'Show the step-by-step guide' in main_xml and
      '2. Recorded actions (optional)' in main_xml and
      'More actions (advanced)' in main_xml and
      'x:Name="TeachButton"' in main_xml and 'x:Name="RepairButton"' in main_xml,
      "Websites screen provides beginner steps and retains optional advanced actions")
check('1. Which website?' in wizard_xml and
      '2. Where should it open?' in wizard_xml and
      '3. Automatic login (optional)' in wizard_xml and
      'Advanced: connection type and port (optional)' in wizard_xml and
      'DataTrigger Binding="{Binding IsChecked, ElementName=AutoLoginCheck}" Value="True"' in wizard_xml and
      all(f'x:Name="{n}"' in wizard_xml for n in
          ('ProtocolCombo','PortBox','UrlBox','NameBox','AutoLoginCheck','UsernameBox','PasswordBox','BrowserCombo','MonitorCombo')),
      "wizard keeps required data fields and hides optional login controls")
check('Click Add website.' in tutorial_code and 'Run all opens every enabled website.' in tutorial_code,
      "getting-started tutorial reflects the revised workflow")
check('Text="No websites yet"' in main_xml and
      'Currently selected website:' in main_xml and
      'How should LaunchFlow sign in?' in main_xml and
      'Click="OpenWebsiteAdvancedSignIn_Click"' in main_xml and
      'SelectionChanged="SettingsTabs_SelectionChanged"' in main_xml and
      'Older workspace groups' in main_xml and
      'WebsiteLoginMode.UsesSavedPassword' in source and
      'WebsiteNextStepGuidance.ForCreatedWebsite' in source and
      named['AutoLoginCheck'].get('Visibility') == 'Collapsed',
      "website-focused UI, one sign-in mode and Settings category navigation are present")
# Settings owns the password editor; the main header must not duplicate it.
presentation_ns = "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}"
main_tab_headers = [item.get("Header") for item in named["MainTabs"] if item.tag == presentation_ns + "TabItem"]
settings_tab_headers = [item.get("Header") for item in named["SettingsTabs"] if item.tag == presentation_ns + "TabItem"]
check(main_tab_headers == ["Websites", "Settings"] and
      settings_tab_headers[:3] == ["General", "Passwords", "Startup"] and
      "PasswordsNav" not in named and
      'Content="Startup &amp; background"' not in main_xml and
      'Content="Security &amp; KeePassXC"' not in main_xml and
      'SettingsTabs.SelectedIndex == 1' in source and
      'UpdatePasswordSettingsAccess()' in source,
      "Settings has one category list with a gated Passwords editor, no shortcut tile duplication")
check('FirstWebsiteGuidanceShown' in source and
      '_configuration.Workflows.Count == 0' in source and
      'if (firstWebsite)' in source and
      'FirstWebsiteGuidanceShown' in (root / "LaunchFlow/Models/Configuration.cs").read_text(encoding="utf-8"),
      "post-create guidance is shown once for first website and persisted in vault configuration")
check('HeaderVersionLabel.Text = "v" + AppInfo.VersionString' in source
      and 'CurrentVersionLabel.Text = "Installed version " + AppInfo.VersionString' in source,
      "UI version labels use assembly version instead of hardcoded display text")
csproj = ET.parse(root / "LaunchFlow/LaunchFlow.csproj")
version = csproj.findtext("./PropertyGroup/Version")
iss = (root / "installer/LaunchFlow.iss").read_text(encoding="utf-8")
info = (root / "LaunchFlow/Services/AppInfo.cs").read_text(encoding="utf-8")
check(version == "0.7.34" and f'#define AppVersion "{version}"' in iss
      and f'return v is null ? "{version}"' in info,
      "assembly, installer and fallback version match 0.7.34")
guide = ET.parse(root / "LaunchFlow/TutorialWindow.xaml").getroot()
guide_source = (root / "LaunchFlow/TutorialWindow.xaml.cs").read_text(encoding="utf-8")
check('new TutorialWindow { Owner = this }.ShowDialog()' not in source and
      'OpenTutorial();' in source and
      'Show() is modeless' in source and
      'ExampleAction' in ET.tostring(guide, encoding="unicode") and
      'ExampleAction.Text' in guide_source,
      "Help is interactive and provides a privacy-safe visual example")
recorder = ET.parse(root / "LaunchFlow/VisualRecorderWindow.xaml").getroot()
recorder_xml = ET.tostring(recorder, encoding="unicode")
recorder_source = (root / "LaunchFlow/VisualRecorderWindow.xaml.cs").read_text(encoding="utf-8")
check('SelectionMode="Extended"' in recorder_xml and
      'Click="SelectAll_Click"' in recorder_xml and
      'Click="EditSelected_Click"' in recorder_xml and
      'Click="QuickWait_Click"' in recorder_xml and
      'DrainActions()' in recorder_source,
      "Recorder supports multi-select, safe recorded keys, editing and page waits")
check('SelectionMode="Extended"' in ET.tostring(ui, encoding="unicode") and
      'Click="SelectAllSteps_Click"' in ET.tostring(ui, encoding="unicode") and
      'Click="QuickWaitStep_Click"' in ET.tostring(ui, encoding="unicode") and
      'SaveStatusLabel' in named and
      'MarkUnsavedChanges()' in source and
      'ConstrainMaximizedWindowToWorkArea' in source,
      "Website steps have bulk editing, save feedback and monitor-safe maximize")
check('HasPendingEditorDialog' in source and
      'private bool ShowTrackedStepEditor(StepEditorWindow editor)' in source and
      source.count('!ShowTrackedStepEditor(editor)') == 3 and
      'if (_hasUnsavedChanges || HasPendingEditorDialog)' in source and
      'if (HasPendingEditorDialog)' in source,
      "installer and explicit exit refuse a still-open website wizard, step editor or Teach recording")
check('ExitSafety.TryPersistForExit(' in source and
      'if (_installerShutdownInProgress || _exiting || !TryCloseWithSavedChanges()) return;' in source and
      'if (!TryCloseWithSavedChanges())' in source and
      'if (_hasUnsavedChanges)' in source and
      'MarkUnsavedChanges();\n        AppendLog("Workflow duplicated' in source and
      '_store.SaveDurable(_configuration);\n            ClearCredentialEditor();' in source,
      "explicit tray/window exits refuse dirty locked saves, credential preference writes are durable and duplicates become dirty")
check('if (_hasUnsavedChanges && !VaultDatabase.Current.IsUnlocked)' in source and
      'if (_vaultCleanupFailed || _vaultLockInProgress)' in source and
      'if (_installerProfileSealFailed)' in source and
      'Unlock LaunchFlow and click Save before updating.' in source and
      'if (VaultDatabase.Current.IsUnlocked && !SaveAll())' in source,
      "installer refuses dirty locked-vault edits and distinguishes browser/profile cleanup failures")
check('IsCurrentStepEditorOwner(WebsiteWorkflow workflow, long revision)' in source and
      'WorkflowEditGuard.IsCurrent(_configuration, _selected, workflow, current,' in source and
      'index >= 0 && index < original.Steps.Count' in
      (root / "LaunchFlow/Services/WorkflowEditGuard.cs").read_text(encoding="utf-8") and
      'var revision = _workspaceRevision;' in source and
      'ReferenceEquals(_selected, workflow)' in source,
      "modal step editing cannot modify a detached or imported workflow")
check('KeepassEntryBox.TextChanged += (_, _) => MarkUnsavedChanges();' in source and
      'SaveStatusLabel.Text = "Unsaved changes"' in source,
      "manual KeePass entry path is tracked as an unsaved configuration edit")
teachjs = (root / "LaunchFlow/Services/TeachRecorderScript.cs").read_text(encoding="utf-8")
teachcs = (root / "LaunchFlow/Services/TeachRecorder.cs").read_text(encoding="utf-8")
check("event.key === 'Enter'" in teachjs and
      "event.shiftKey || event.ctrlKey || event.altKey || event.metaKey" in teachjs and
      "target.getRootNode() !== document" in teachjs and
      "field instanceof Element" in teachjs and
      "sensitiveHints" in teachjs and
      "UnsupportedTargetObserved" in teachcs,
      "keyboard recorder rejects changed modifiers and sensitive/shadow-root contexts")
check("https://api.github.com/repos/KamiBuilds/LaunchFlow/releases/latest" in info,
      "updater still targets published stable GitHub releases")
print(f"{len(errors)} visual/release contract errors")
sys.exit(bool(errors))
