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
required = {"MainTabs", "WorkflowsNav", "PasswordsNav", "SettingsNav",
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
      'Content="+ New website"' in markup and
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
      'VaultDatabase.Current.ReplaceSnapshot(imported.Config, imported.Credentials);\n            _workspaceRevision++;' in source,
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
check('HeaderVersionLabel.Text = "v" + AppInfo.VersionString' in source
      and 'CurrentVersionLabel.Text = "Installed version " + AppInfo.VersionString' in source,
      "UI version labels use assembly version instead of hardcoded display text")
csproj = ET.parse(root / "LaunchFlow/LaunchFlow.csproj")
version = csproj.findtext("./PropertyGroup/Version")
iss = (root / "installer/LaunchFlow.iss").read_text(encoding="utf-8")
info = (root / "LaunchFlow/Services/AppInfo.cs").read_text(encoding="utf-8")
check(version == "0.7.23" and f'#define AppVersion "{version}"' in iss
      and f'return v is null ? "{version}"' in info,
      "assembly, installer and fallback version match 0.7.23")
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
check('if (_hasUnsavedChanges && !VaultDatabase.Current.IsUnlocked)' in source and
      'if (_hasUnsavedChanges || _vaultCleanupFailed' in source and
      'Unlock LaunchFlow and click Save before updating.' in source and
      'if (VaultDatabase.Current.IsUnlocked && !SaveAll())' in source,
      "installer refuses automatic shutdown with unsaved locked-vault edits")
check('IsCurrentStepEditorOwner(WebsiteWorkflow workflow, long revision)' in source and
      'index >= workflow.Steps.Count || !ReferenceEquals(workflow.Steps[index], current)' in source and
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
