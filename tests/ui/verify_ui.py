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

views = [p for p in (root / "LaunchFlow").rglob("*.xaml") if "bin" not in p.parts and "obj" not in p.parts]
for view in views:
    ET.parse(view)
check(len(views) >= 10, "WPF views are well-formed XML")

ui = ET.parse(root / "LaunchFlow/UI/Shell/MainWindow.xaml").getroot()
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
# Keep the primary Run / Stop group truly centered at every window width.
toolbar = named.get("ActionToolbar")
presentation = "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}"
grid_column = "Grid.Column"  # XAML attached property is an unprefixed XML attribute
columns = toolbar.find(presentation + "Grid.ColumnDefinitions") if toolbar is not None else None
direct_children = list(toolbar) if toolbar is not None else []
centred_group = [child for child in direct_children if child.get(grid_column) == "1"]
right_group = [child for child in direct_children if child.get(grid_column) == "2"]
check(toolbar is not None and
      toolbar.get("SizeChanged") == "ActionToolbar_SizeChanged" and
      columns is not None and
      [child.get("Width") for child in columns] == ["*", "Auto", "*"] and
      len(centred_group) == 1 and
      any(node.get(xns + "Name") == "RunAllButton" for node in centred_group[0].iter()) and
      any(node.get(xns + "Name") == "StopButton" for node in centred_group[0].iter()) and
      len(right_group) == 1 and
      any(node.get("Content") == "Getting started" for node in right_group[0].iter()) and
      any(node.get("Content") == "About" for node in right_group[0].iter()),
      "toolbar has symmetric sides, centered primary actions and right-aligned help")
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
# Inspect all production MainWindow partials, not a single oversized code-behind file.
source = "\n".join(file.read_text(encoding="utf-8")
                   for file in sorted((root / "LaunchFlow" / "UI" / "Shell").glob("MainWindow*.cs")))
check(all(handler in source for handler in
          ("WorkspaceCombo_SelectionChanged", "SaveWorkspace_Click",
           "PreviewWorkspace_Click", "RunWorkspace_Click")),
      "workspace selection, save, preview and run handlers present")
check('WorkspaceCombo.Text.Trim()' not in source,
      "workspace execution no longer relies on ComboBox text")
check('private void ActionToolbar_SizeChanged' in source and
      'e.NewSize.Width < 940' in source,
      "responsive toolbar keeps compact views free of overlapping status text")
autosave_code = (root / "LaunchFlow/UI/Shell/MainWindow.AutoSave.cs").read_text(encoding="utf-8")
autosave_schedule = (root / "LaunchFlow/Services/Persistence/AutoSaveSchedule.cs").read_text(encoding="utf-8")
check('StartAutoSave();' in source and
      '_autoSaveSchedule.Changed(DateTimeOffset.UtcNow)' in source and
      'private void AutoSaveTimer_Tick' in autosave_code and
      'SaveAll()' in autosave_code and
      'SaveStatusLabel.Text = "Saving…"' in autosave_code and
      'SaveStatusLabel.Text = "Save failed"' in autosave_code and
      'ShowAutoSaveFailed();' in source and
      '_store.SaveDurable(_configuration)' in source and
      'private bool _autoSaving' in autosave_code,
      "auto-save schedules edits and confirms encrypted durable writes")
check('EnabledCheck.Checked += (_, _) => WebsiteEnabledChanged()' in source and
      'EnabledCheck.Unchecked += (_, _) => WebsiteEnabledChanged()' in source and
      'PersistPendingAutoSave(immediate: true)' in autosave_code and
      '!_runUi.IsRunning && VaultDatabase.Current.IsUnlocked' in autosave_code,
      "website enabled toggle commits immediately unless blocked by vault/run safety")
approval = ET.parse(root / "LaunchFlow/UI/Dialogs/PasswordOriginApprovalWindow.xaml").getroot()
approval_markup = ET.tostring(approval, encoding="unicode")
approval_code = (root / "LaunchFlow/UI/Dialogs/PasswordOriginApprovalWindow.xaml.cs").read_text(encoding="utf-8")
approval_coordinator = (root / "LaunchFlow/Services/Persistence/PasswordOriginApprovalCoordinator.cs").read_text(encoding="utf-8")
check(approval.get("Topmost") == "True" and
      approval.get("ShowActivated") == "True" and
      approval.get("WindowStartupLocation") == "CenterScreen" and
      'IsDefault="True"' in approval_markup and
      'IsCancel="True"' in approval_markup and
      'new PasswordOriginApprovalWindow(workflow.Name, newOrigin)' in source and
      'new PasswordOriginApprovalCoordinator(_saveTransaction).Request(' in source and
      'return dialog.ShowDialog() == true;' in source and
      'if (!stillOwns()) return new(Outcome.Invalidated);' in approval_coordinator and
      'if (!showNativeConsent()) return new(Outcome.Declined);' in approval_coordinator and
      'saveTransaction.Commit(' in approval_coordinator and
      'requestedOrigin.TrimEnd(' in approval_coordinator and
      'DialogResult = true;' in approval_code and
      'Activate();' in approval_code,
      "password-origin approval is foreground, opt-in and revalidated after modal consent")
check('UsernameBox.TextChanged += (_, _) => MarkUnsavedChanges()' not in source and
      'PasswordInput.PasswordChanged += (_, _) => MarkUnsavedChanges()' not in source and
      'public void Attempted()' in autosave_schedule and
      'public void Saved()' in autosave_schedule,
      "auto-save excludes raw credentials and prevents failed-write retry storms")
check('WorkspaceCatalog.Save(_configuration, name' in source and
      'WorkspaceCatalog.IsCurrentSelection' in source and
      'PreviewWorkspaceButton.IsEnabled = available && savedWorkspaceSelected' in source,
      "workspace save, preview and run share rollback-safe validated selection")
check('WorkspaceCatalog.ConfirmationStillValid(' in source and
      '_workspaceRevision++' in source and
      'VaultDatabase.Current.ReplaceSnapshot(projected, imported.Credentials, capturedSession);' in source and
      'VaultDatabase.Current.SessionGeneration != capturedSession' in source and
      'ImportConfigurationProjection.ForThisDevice(' in source and
      '_workspaceRevision++;' in source,
      "encrypted import invalidates modal workspace overwrite approval")
check('WorkspaceCatalog.PendingRunIsCurrent(' in source and
      'launchRevision = _workspaceRevision' in source and
      'WorkspaceCombo.SelectedItem, workspace' in source and
      'SaveDurable(_configuration)' in source,
      "modal workflow dispatch rechecks current workspace and saves are durable")
store = (root / "LaunchFlow/Services/Persistence/ConfigStore.cs").read_text(encoding="utf-8")
check('public void SaveDurable(Configuration configuration)' in store and
      'public void SaveDurable(Configuration configuration, long expectedSession)' in store and
      '_vault.SaveConfiguration(configuration, expectedSession)' in store and
      'SaveConfigurationUnderGate(config, expectedSession)' in
          (root / "LaunchFlow/Services/Vault/VaultDatabase.cs").read_text(encoding="utf-8"),
      "durable workspace persistence checks the expected vault session under its gate")
extractor = (root / "tests/workspaces/extract-ui-methods.py").read_text(encoding="utf-8")
check(extractor.count('read_text(encoding="utf-8")') >= 2 and
      'write_text(output, encoding="utf-8")' in extractor,
      "Windows UI harness reads and writes explicit UTF-8")
wizard = ET.parse(root / "LaunchFlow/UI/Dialogs/NewWorkflowWindow.xaml").getroot()
wizard_named = {el.get(xns + "Name"): el for el in wizard.iter() if el.get(xns + "Name")}
name_box = wizard_named["NameBox"]
name_hint = wizard_named["NamePlaceholder"]
name_hint_style = name_hint.find(presentation + "TextBlock.Style/" + presentation + "Style")
name_hint_trigger = name_hint_style.find(presentation + "Style.Triggers/" + presentation + "DataTrigger")
check(name_box.get("Text") == "" and
      name_hint.get("Text") == "My website" and
      name_hint.get("IsHitTestVisible") == "False" and
      name_hint_trigger.get("Binding") == "{Binding Text, ElementName=NameBox}" and
      name_hint_trigger.get("Value") == "" and
      any(setter.get("Property") == "Visibility" and setter.get("Value") == "Visible"
          for setter in name_hint_trigger) and
      'string.IsNullOrWhiteSpace(NameBox.Text) ? "My website" : NameBox.Text.Trim()' in
      (root / "LaunchFlow/UI/Dialogs/NewWorkflowWindow.xaml.cs").read_text(encoding="utf-8"),
      "new website name starts empty and its visual hint cannot become part of typed text")
check(wizard_named["AutoLoginCheck"].get("IsChecked") == "False" and
      "No login required" in ET.tostring(wizard, encoding="unicode") and
      'LoginMode = autoLogin ? "Auto" : "None"' in
      (root / "LaunchFlow/UI/Dialogs/NewWorkflowWindow.xaml.cs").read_text(encoding="utf-8"),
      "new local websites default to explicit No login required without credential prompts")
# Username/email is intentionally readable in the create-website wizard.
# The password must remain a WPF PasswordBox and must never be read as ordinary Text.
wizard_code = (root / "LaunchFlow/UI/Dialogs/NewWorkflowWindow.xaml.cs").read_text(encoding="utf-8")
check(wizard_named["UsernameBox"].tag.endswith("}TextBox") and
      wizard_named["PasswordBox"].tag.endswith("}PasswordBox") and
      'Username / email (hidden)' not in ET.tostring(wizard, encoding="unicode") and
      'UsernameBox.Text.Trim()' in wizard_code and
      'UsernameBox.Password' not in wizard_code and
      'PasswordBox.Password' in wizard_code and
      'NewUsername = autoLogin ? UsernameBox.Text.Trim() : "";' in wizard_code and
      'NewPassword = autoLogin ? PasswordBox.Password : "";' in wizard_code and
      'if (!WasCreated) ClearPendingSecrets();' in wizard_code,
      "website wizard displays username/email in plaintext, keeps password masked and retains secret cleanup")
manager_source = (root / "LaunchFlow/UI/Shell/MainWindow.PasswordManager.cs").read_text(encoding="utf-8")
credential_source = (root / "LaunchFlow/UI/Shell/MainWindow.Credentials.cs").read_text(encoding="utf-8")
editor_source = (root / "LaunchFlow/UI/Shell/MainWindow.WebsiteEditor.cs").read_text(encoding="utf-8")
check({"PasswordWebsiteCombo", "SaveCredentialsButton", "ClearCredentialButton"}.issubset(named) and
      named["PasswordWebsiteCombo"].get("DisplayMemberPath") is None and
      named["PasswordWebsiteCombo"].find(
          "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}ComboBox.ItemTemplate/"
          "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}DataTemplate/"
          "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}TextBlock"
      ).get("Text") == "{Binding Name}" and
      not {"PasswordSiteName", "PasswordSiteUrl", "CredentialScopeNotice"}.intersection(named) and
      named["PasswordWebsiteCombo"].get("SelectionChanged") == "PasswordWebsiteCombo_SelectionChanged" and
      named["UsernameBox"].tag.endswith("}TextBox") and
      named["PasswordInput"].tag.endswith("}PasswordBox") and
      'SyncPasswordWebsiteSelector();' in editor_source and
      'ReferenceEquals(PasswordWebsiteCombo.SelectedItem, _editorSession.Selection)' in credential_source and
      'UsernameBox.Text.Trim()' in credential_source and
      'UsernameBox.Password' not in credential_source and
      'private void PasswordWebsiteCombo_SelectionChanged' in manager_source and
      'Unsaved login changes' in manager_source,
      "Password Manager displays website names without redundant warnings and preserves credential scope guards")
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
      "'--exit-for-install'" in iss_source and 'Sleep(250)' in iss_source and
      'for Attempt := 1 to 960 do' in iss_source,
      "installer waits up to 240s for graceful exit and retains mutex protection")
exec_source = (root / "LaunchFlow/UI/Shell/MainWindow.Execution.cs").read_text(encoding="utf-8")
firefox_ready_fn = exec_source.split("private bool EnsureFirefoxAutomationReady", 1)[1].split(
    "private void OfferFirefoxAutomationHelp", 1)[0]
check("if (PlaywrightFirefoxSupport.IsAutomationFirefoxInstalled())" in firefox_ready_fn and
      "return true;" in firefox_ready_fn and
      firefox_ready_fn.index("return true;") <
      firefox_ready_fn.index("Run cancelled: Firefox is not ready") and
      "WorkflowStatusLabel.Text = LocalizationService.Text(\"Failed\"" in firefox_ready_fn and
      firefox_ready_fn.index("return true;") <
      firefox_ready_fn.index("WorkflowStatusLabel.Text = LocalizationService.Text(\"Failed\""),
      "Firefox readiness consent success does not mark the run Failed before continuing")
style = (root / "LaunchFlow/App.xaml").read_text(encoding="utf-8")
check('SelectionBoxItemTemplate' in style and 'PART_Popup' in style
      and 'ItemsPresenter' in style,
      "dropdown renders display-member templates and accessible popup items")
browser_setup = ET.parse(root / "LaunchFlow/UI/Dialogs/BrowserSetupWindow.xaml").getroot()
setup_scroll = browser_setup.find(presentation + "ScrollViewer")
setup_names = {node.get(xns + "Name"): node for node in browser_setup.iter()
               if node.get(xns + "Name")}
check(browser_setup.get("ResizeMode") == "CanResize" and
      setup_scroll is not None and
      setup_scroll.get("VerticalScrollBarVisibility") == "Auto" and
      setup_scroll.get("HorizontalScrollBarVisibility") == "Disabled" and
      all(setup_names[name].get("TextWrapping") == "Wrap" for name in
          ("ReadinessText", "DetailText", "FirefoxStatusText")) and
      setup_names["BrowserCheckAgainButton"].get("Content") == "Scan again",
      "browsers dialog resizes and scrolls long discovery paths without clipping controls")
firefox_support = (root / "LaunchFlow/Services/Browser/PlaywrightFirefoxSupport.cs").read_text(encoding="utf-8")
check('ResolveLaunchBrowsersPath(' in firefox_support and
      'DefaultMsPlaywrightCachePath()' in firefox_support and
      'Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", selected)' in firefox_support and
      'InstalledBrowsersPath' in firefox_support,
      "Firefox readiness and launch resolve the same supported browser cache")
check('BrowserChoices.Refresh(BrowserCombo, w.Browser)' in source and
      'BrowserCombo_DropDownOpened' in ET.tostring(ui, encoding="unicode") and
      'BrowserInstallations.IsInstalled(browser)' in (root / "LaunchFlow/UI/Dialogs/NewWorkflowWindow.xaml.cs").read_text(encoding="utf-8"),
      "both browser selectors use installation detection and retain configured browser identity")
check('ObserveLoginCompletionAsync' in "\n".join(p.read_text(encoding="utf-8") for p in sorted((root / "LaunchFlow/Services/Automation").glob("AutomationEngine*.cs"))) and
      'VerifiedRedirectAsync' in (root / "LaunchFlow/Services/Login/LoginHealth.cs").read_text(encoding="utf-8") and
      'var wasLoading = _loadingEditor;' in source and
      'CloseSessionsRetry' in source,
      "sign-in verification and safe installer cleanup recovery remain wired")
teach_recorder = (root / "LaunchFlow/Services/Teach/TeachRecorder.cs").read_text(encoding="utf-8")
check('SignInDecisionWindow.Decision.Confirmed' in source and
      (root / "LaunchFlow/UI/Dialogs/SignInDecisionWindow.xaml").is_file() and
      'ConfirmSignedInAsync' in "\n".join(p.read_text(encoding="utf-8") for p in sorted((root / "LaunchFlow/Services/Automation").glob("AutomationEngine*.cs"))) and
      'session._pendingActions.Enqueue(("Navigate", safeUrl))' in teach_recorder and
      'safeToTeachNavigate' in teach_recorder and
      'crossedSite' in teach_recorder and
      'GetLeftPart(UriPartial.Path)' in teach_recorder,
      "human sign-in confirmation and automatic cross-site Teach navigation keep same-site clicks")
check('dialog.Show();' in source and
      'ApplyModelessSignInDecisionAsync' in source and
      'decision.ShowDialog();' not in source and
      'new AboutWindow { Owner = this }.Show();' in source and
      'new BrowserSetupWindow { Owner = this }.Show();' in source,
      "noncritical windows remain modeless while dedicated authorization prompts stay gated")
check('CredentialDeleteGuard.IsCurrent(' in source and
      'ImportConfigurationProjection.ForThisDevice(' in source and
      'PortableImportValidation.Validate(package)' in
      (root / "LaunchFlow/Services/Transfer/SecureTransferService.cs").read_text(encoding="utf-8") and
      'RequireHttpUrl(' in
      (root / "LaunchFlow/Services/Transfer/PortableImportValidation.cs").read_text(encoding="utf-8") and
      'recorded actions cannot carry typed values' in
      (root / "LaunchFlow/Services/Transfer/PortableImportValidation.cs").read_text(encoding="utf-8") and
      'KeePassDatabasePath = null' in
      (root / "LaunchFlow/Services/Persistence/ImportConfigurationProjection.cs").read_text(encoding="utf-8") and
      'DisplayLabel' in
      (root / "LaunchFlow/Domain/Models/WorkflowStep.cs").read_text(encoding="utf-8") and
      'DisplayMemberPath="DisplayLabel"' in
      (root / "LaunchFlow/UI/Shell/MainWindow.xaml").read_text(encoding="utf-8") and
      'safeToTeachNavigate' in
      (root / "LaunchFlow/Services/Teach/TeachRecorder.cs").read_text(encoding="utf-8") and
      'Unsupported vault table identifier' in
      (root / "LaunchFlow/Services/Vault/VaultDatabase.cs").read_text(encoding="utf-8") and
      'VaultDatabase.Current.ReplaceSnapshot(projected, imported.Credentials, capturedSession)' in source and
      '_configuration = projected;' in source and
      'SealProfilesCheck.IsChecked = _configuration.SealBrowserProfilesOnLock;' in source and
      'KeePassDbBox.Text = _configuration.KeePassDatabasePath ?? "";' in source,
      "safe deletion, import validation and projected configuration remain wired to storage and UI")
check('WindowsApplicationControl.CheckRequiredComponents()' in (root / "LaunchFlow/App.xaml.cs").read_text(encoding="utf-8") and
      'Microsoft.Data.Sqlite' in (root / "LaunchFlow/Services/Workspaces/WindowsApplicationControl.cs").read_text(encoding="utf-8"),
      "startup checks blocked DLLs before new vault setup")
wizard_xml = (root / "LaunchFlow/UI/Dialogs/NewWorkflowWindow.xaml").read_text(encoding="utf-8")
main_xml = (root / "LaunchFlow/UI/Shell/MainWindow.xaml").read_text(encoding="utf-8")
main_ui_tree = ET.parse(root / "LaunchFlow/UI/Shell/MainWindow.xaml").getroot()
check('x:Name="EmptyStepsHint"' in main_xml and
      'No clicks yet. Press' in main_xml and
      'UpdateEmptyStepsHint()' in
      (root / "LaunchFlow/UI/Shell/MainWindow.Steps.cs").read_text(encoding="utf-8") and
      'ControlTemplate TargetType="ListBox"' in
      (root / "LaunchFlow/App.xaml").read_text(encoding="utf-8"),
      "empty click list has a dark placeholder instead of a blank white box")
check('x:Name="EditSelectedWebsiteLoginButton"' in main_xml and
      'Click="EditSelectedWebsiteLogin_Click"' in main_xml and
      main_xml.index('2. Website sign-in') < main_xml.index('3. Clicks after sign-in') and
      'Content="Select all"' in main_xml and
      'Content="Delete selected"' in main_xml and
      'Visibility="Collapsed"' in main_xml and
      'Header="Special help"' in main_xml and
      'SettingsTabs.SelectedIndex = 1;' in
      (root / "LaunchFlow/UI/Shell/MainWindow.Navigation.cs").read_text(encoding="utf-8"),
      "website sign-in stays simple; record tools and delete are visible; Special help is hidden")
tutorial_code = (root / "LaunchFlow/UI/Dialogs/TutorialWindow.xaml.cs").read_text(encoding="utf-8")
check('START HERE · 3 EASY STEPS' in main_xml and
      'Show the step-by-step guide' in main_xml and
      '3. Clicks after sign-in' in main_xml and
      'Content="Select all"' in main_xml and
      'x:Name="TeachButton"' in main_xml and 'x:Name="RepairButton"' in main_xml,
      "Websites screen provides beginner steps with visible record/edit tools")
check('1. Which website?' in wizard_xml and
      '2. Where should it open?' in wizard_xml and
      '3. Sign in (optional)' in wizard_xml and
      'Extra address options (usually not needed)' in wizard_xml and
      'DataTrigger Binding="{Binding IsChecked, ElementName=AutoLoginCheck}" Value="True"' in wizard_xml and
      all(f'x:Name="{n}"' in wizard_xml for n in
          ('ProtocolCombo','PortBox','UrlBox','NameBox','AutoLoginCheck','UsernameBox','PasswordBox','BrowserCombo','MonitorCombo')),
      "wizard keeps required data fields and hides optional login controls")
check('new("BASIC-02", "Open website setup"' in tutorial_code and
      'Click + Add website.' in tutorial_code and
      'Save for selected website' in tutorial_code and
      'new("RECORD-02", "Start the recorder"' in tutorial_code and
      'new("BASIC-10", "Test only that website"' in tutorial_code and
      'Next does not mark a task complete.' in tutorial_code,
      "getting-started guide maps real website, credential and recorder controls to one-action steps")
check('Text="No websites yet"' in main_xml and
      'Choose exactly which website account to edit' in main_xml and
      'How should LaunchFlow sign in?' in main_xml and
      'x:Name="AdvancedSignInButton"' in main_xml and
      'Visibility="Collapsed"' in main_xml and
      'SelectionChanged="SettingsTabs_SelectionChanged"' in main_xml and
      'Older workspace groups' in main_xml and
      'WebsiteLoginMode.UsesSavedPassword' in source and
      'WebsiteNextStepGuidance.ForCreatedWebsite' in source and
      named['AutoLoginCheck'].get('Visibility') == 'Collapsed',
      "website-focused UI hides Special CSS help from normal users")
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
      'FirstWebsiteGuidanceShown' in (root / "LaunchFlow/Domain/Models/Configuration.cs").read_text(encoding="utf-8"),
      "post-create guidance is shown once for first website and persisted in vault configuration")
check('HeaderVersionLabel.Text = "v" + AppInfo.VersionString' in source
      and 'CurrentVersionLabel.Text = "Installed version " + AppInfo.VersionString' in source,
      "UI version labels use assembly version instead of hardcoded display text")
csproj = ET.parse(root / "LaunchFlow/LaunchFlow.csproj")
version = csproj.findtext("./PropertyGroup/Version")
iss = (root / "installer/LaunchFlow.iss").read_text(encoding="utf-8")
info = (root / "LaunchFlow/Services/Platform/AppInfo.cs").read_text(encoding="utf-8")
check(bool(version) and f'#define AppVersion "{version}"' in iss
      and f'return v is null ? "{version}"' in info,
      f"assembly, installer and fallback version match {version}")
guide = ET.parse(root / "LaunchFlow/UI/Dialogs/TutorialWindow.xaml").getroot()
guide_source = (root / "LaunchFlow/UI/Dialogs/TutorialWindow.xaml.cs").read_text(encoding="utf-8")
check('new TutorialWindow { Owner = this }.ShowDialog()' not in source and
      'OpenTutorial(' in source and
      'Show() is modeless' in source and
      'ExampleAction' in ET.tostring(guide, encoding="unicode") and
      'ExampleAction.Text' in guide_source,
      "Help is interactive and provides a privacy-safe visual example")
recorder = ET.parse(root / "LaunchFlow/UI/Dialogs/VisualRecorderWindow.xaml").getroot()
recorder_xml = ET.tostring(recorder, encoding="unicode")
recorder_raw = (root / "LaunchFlow/UI/Dialogs/VisualRecorderWindow.xaml").read_text(encoding="utf-8")
recorder_source = (root / "LaunchFlow/UI/Dialogs/VisualRecorderWindow.xaml.cs").read_text(encoding="utf-8")
check('SelectionMode="Extended"' in recorder_xml and
      'Click="SelectAll_Click"' in recorder_xml and
      'Click="EditSelected_Click"' in recorder_xml and
      'Click="QuickWait_Click"' in recorder_xml and
      'Text="RECORDING"' in recorder_raw and
      'x:Name="RecordingBanner"' in recorder_raw and
      'More options (optional)' not in recorder_raw and
      'DrainActions()' in recorder_source and
      'StartRecordingPulse()' in recorder_source,
      "Recorder shows a live RECORDING cue with visible select/delete tools")
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
      'if (_editorSession.IsDirty || HasPendingEditorDialog)' in source and
      'if (HasPendingEditorDialog)' in source,
      "installer and explicit exit refuse a still-open website wizard, step editor or Teach recording")
check('ExitSafety.TryPersistForExit(' in source and
      'if (_installerShutdownInProgress || _exiting || !TryCloseWithSavedChanges()) return;' in source and
      'if (!TryCloseWithSavedChanges())' in source and
      'if (_editorSession.IsDirty)' in source and
      'DuplicateWorkflow_Click' in source and
      '_store.SaveDurable(candidate, ownedVaultSession);' in source and
      'Duplicated' in source and
      '_vault.SaveWithConfiguration(workflow.Id, new SiteCredential(username, password),' in source and
      '_store.NoteCommittedExternally();' in source and
      'candidateConfig.Workflows[index].AutoLogin = autoLogin;' in source,
      "explicit tray/window exits refuse dirty locked saves, credential preference writes are durable and duplicates become dirty")
lifecycle_source = (root / "LaunchFlow/UI/Shell/MainWindow.Lifecycle.cs").read_text(encoding="utf-8")
check('if (_editorSession.IsDirty && !VaultDatabase.Current.IsUnlocked)' in source and
      'if (_vaultCleanupFailed || _vaultLockInProgress)' in lifecycle_source and
      'if (installer && _installerProfileSealFailed)' in lifecycle_source and
      'if (_profileSealFailed)' in lifecycle_source and
      'Encrypted-profile sealing failed.' in lifecycle_source and
      'Browser session cleanup did not finish safely.' in lifecycle_source and
      'Unlock LaunchFlow and save your edits before updating.' in source and
      'if (_editorSession.IsDirty && !SaveAll())' in source and
      'if (_editorSession.IsDirty && VaultDatabase.Current.IsUnlocked && !SaveAll())' in source and
      'await CompleteOwnedExitAsync(installer: true)' in source,
      "installer refuses dirty locked-vault edits and distinguishes browser/profile cleanup failures")
check('IsCurrentStepEditorOwner(WebsiteWorkflow workflow, long revision)' in source and
      'WorkflowEditGuard.IsCurrent(_configuration, _editorSession.Selection, workflow, current,' in source and
      'index >= 0 && index < original.Steps.Count' in
      (root / "LaunchFlow/Services/Persistence/WorkflowEditGuard.cs").read_text(encoding="utf-8") and
      'var revision = _workspaceRevision;' in source and
      'ReferenceEquals(_editorSession.Selection, workflow)' in source,
      "modal step editing cannot modify a detached or imported workflow")
check('KeepassEntryBox.TextChanged += (_, _) => MarkUnsavedChanges();' in source and
      'SaveStatusLabel.Text = "Unsaved changes"' in source,
      "manual KeePass entry path is tracked as an unsaved configuration edit")
teachjs = (root / "LaunchFlow/Services/Teach/TeachRecorderScript.cs").read_text(encoding="utf-8")
teachcs = (root / "LaunchFlow/Services/Teach/TeachRecorder.cs").read_text(encoding="utf-8")
check("event.key === 'Enter'" in teachjs and
      "event.shiftKey || event.ctrlKey || event.altKey || event.metaKey" in teachjs and
      "target.getRootNode() !== document" in teachjs and
      "field instanceof Element" in teachjs and
      "sensitiveHints" in teachjs and
      "UnsupportedTargetObserved" in teachcs,
      "keyboard recorder rejects changed modifiers and sensitive/shadow-root contexts")
check("https://api.github.com/repos/KamiBuilds/LaunchFlow/releases/latest" in info,
      "updater still targets published stable GitHub releases")
sign_in = ET.parse(root / "LaunchFlow/UI/Dialogs/SignInDecisionWindow.xaml").getroot()
sign_in_markup = ET.tostring(sign_in, encoding="unicode")
check(int(sign_in.get("Height", "0")) >= 280 and
      "VerticalScrollBarVisibility=\"Auto\"" in sign_in_markup and
      'x:Name="ReasonText"' not in sign_in_markup and
      "ReasonText" in sign_in_markup and
      "OK, later" in sign_in_markup and "Fix sign-in" in sign_in_markup and
      'Title="Quick check"' in sign_in_markup,
      "sign-in assistance window has enough height, a scrolling reason and visible actions")
check('item.Reason.StartsWith("Password stage still visible"' in source and
      'Password step is open in the browser' in source and
      'ShowFromTray()' not in source[source.find('OfferRepairForLoginAttentionAsync'):source.find('ApplyModelessSignInDecisionAsync')],
      "visible password challenges never trigger misleading signed-in confirmation dialogs")

# P1 shutdown gates: early Windows session veto, bounded waits and retained seal ownership.
exit_lifecycle = (root / 'LaunchFlow/UI/Shell/MainWindow.Lifecycle.cs').read_text(encoding='utf-8')
exit_lock = (root / 'LaunchFlow/UI/Shell/MainWindow.VaultLocking.cs').read_text(encoding='utf-8')
exit_window = (root / 'LaunchFlow/UI/Shell/MainWindow.xaml.cs').read_text(encoding='utf-8')
profile_seal = (root / 'LaunchFlow/Services/Vault/ProfileVaultService.cs').read_text(encoding='utf-8')
check('SessionEnding += OnWindowsSessionEnding' in exit_window and
      'if (!CanPersistForWindowsExit()) e.Cancel = true;' in exit_window and
      'e.Cancel = true;' in exit_window[exit_window.find('private void OnWindowsSessionEnding'):exit_window.find('private void OnClosing')],
      'Windows sign-out rejects failed save before late WPF shutdown')
check('ExitCleanupWait.RequireAsync(_runner.CloseSessionsAsync()' in exit_lock and
      'ExitCleanupWait.RequireAsync(pendingRun' in exit_lock and
      'ExitCleanupWait.RequireAsync(_finalRunnerDisposeTask' in exit_lifecycle and
      '_finalRunnerDisposeTask ??= _runner.DisposeAsync().AsTask()' in exit_lifecycle and
      '_profileSealTask ??= Task.Run(ProfileVaultService.SealAll)' in exit_lock,
      'timeouts retain browser/profile shutdown ownership and block exit')
check('CleanupRetainedSealedDirectories();' in profile_seal and
      'VerifyAuthenticatedSnapshot(sealedPath);' in profile_seal,
      'plaintext quarantine cleanup must authenticate its snapshot before deletion')

print(f"{len(errors)} visual/release contract errors")
sys.exit(bool(errors))
