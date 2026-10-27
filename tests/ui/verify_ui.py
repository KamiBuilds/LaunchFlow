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
check(required.issubset(named), "navigation and workspace controls retained")
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
check(version == "0.7.20" and f'#define AppVersion "{version}"' in iss
      and f'return v is null ? "{version}"' in info,
      "assembly, installer and fallback version match 0.7.20")
check("https://api.github.com/repos/KamiBuilds/LaunchFlow/releases/latest" in info,
      "updater still targets published stable GitHub releases")
print(f"{len(errors)} visual/release contract errors")
sys.exit(bool(errors))
