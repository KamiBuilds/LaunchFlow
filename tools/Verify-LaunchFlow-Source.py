#!/usr/bin/env python3
"""Static regression checks for LaunchFlow source + release packaging.
Run: python tools/Verify-LaunchFlow-Source.py
These checks do not replace compiling on Windows.
"""
from pathlib import Path
import re, sys, xml.etree.ElementTree as ET
root=Path(__file__).resolve().parent.parent
errors=[]

def check(condition,label):
    if not condition: errors.append(label)
    else: print('[OK]',label)

proj=ET.parse(root/'LaunchFlow/LaunchFlow.csproj')
version=proj.findtext('./PropertyGroup/Version')
check(bool(re.fullmatch(r'\d+\.\d+\.\d+', version or '')), 'valid csproj version')
iss=(root/'installer/LaunchFlow.iss').read_text(encoding='utf-8')
check('#define AppVersion "'+version+'"' in iss,'installer matches project version')
check('AppId={{9B1A7C15-093B-4DBA-BDA7-BF43A2E67320}' in iss,'stable upgrade AppId')
check('VersionInfoVersion='+version+'.0' in iss,'installer Windows version')
cs=(root/'LaunchFlow/Services/DarkWindowChrome.cs').read_text(encoding='utf-8')
check('System.Windows.Media.Color.FromRgb' in cs,'WPF Color disambiguation')
app=(root/'LaunchFlow/App.xaml.cs').read_text(encoding='utf-8')
check(app.count('_ = Dispatcher.BeginInvoke(')>=3,'dispatcher callbacks handle file opening, activation and build shutdown')
auto=(root/'LaunchFlow/Services/AutomationEngine.cs').read_text(encoding='utf-8')
check('new Dictionary<string, object?>' not in auto and auto.count('new Dictionary<string, object>')>=2,'Playwright CDP argument nullability')
workflow=(root/'.github/workflows/windows.yml').read_text(encoding='utf-8')
check('LaunchFlow-Setup-v${{ env.LF_VERSION }}-x64.exe' in workflow and 'publish_release' in workflow, 'CI artifact and manual release use csproj version')
check((root/'BUILD-INSTALLER.cmd').is_file() and (root/'BUILD-INSTALLER.ps1').is_file(),'installer builders included')
check((root/'LaunchFlow/Assets/LaunchFlow.ico').is_file(),'app icon present')
for xaml in (root/'LaunchFlow').glob('*.xaml'):
    try: ET.parse(xaml)
    except ET.ParseError as ex: errors.append(f'Invalid XAML XML {xaml.name}: {ex}')
check(len(list((root/'LaunchFlow').glob('*.xaml')))>=5,'XAML files parsed')
main_code=(root/'LaunchFlow/MainWindow.xaml.cs').read_text(encoding='utf-8')
recorder_code=(root/'LaunchFlow/VisualRecorderWindow.xaml.cs').read_text(encoding='utf-8')
check('ShowRecorderAsync(recorder)' in main_code and 'recorder.ShowDialog()' not in main_code,
      'Teach runs modeless so windows can move')
check('ExposeBindingAsync<string>' in recorder_code and 'AddInitScriptAsync' in recorder_code,
      'Teach records clicks through page navigation')
check((root/'LaunchFlow/TutorialWindow.xaml').is_file() and 'new TutorialWindow' in main_code,
      'beginner tutorial is available')
main_code=(root/'LaunchFlow/MainWindow.xaml.cs').read_text(encoding='utf-8')
check('EnsureStartupWorkflowVisible();' in main_code and 'MainTabs.SelectedIndex = -1;' in main_code,
      'initial Workflows content is forced to render after Loaded')
main_code=(root/'LaunchFlow/MainWindow.xaml.cs').read_text(encoding='utf-8')
recorder_code=(root/'LaunchFlow/VisualRecorderWindow.xaml.cs').read_text(encoding='utf-8')
build_script=(root/'BUILD-INSTALLER.ps1').read_text(encoding='utf-8')
check('FirstOrDefault(w => w.Id == workflow.Id)' in main_code,
      'Teach persists clicks only to its original website')
check('_teachingInProgress' in main_code, 'overlapping Teach sessions are blocked')
check('(() => {' in recorder_code and '})()' in recorder_code,
      'recorder page-initialization script is invoked')
check('LaunchFlow-Setup-v$projectVersion-x64.exe' in build_script,
      'builder selects the exact versioned installer')
check('coreclr.dll' in build_script and 'Microsoft.Playwright.dll' in build_script,
      'builder validates bundled application components')
new_workflow=(root/'LaunchFlow/NewWorkflowWindow.xaml.cs').read_text(encoding='utf-8')
new_workflow_xaml=(root/'LaunchFlow/NewWorkflowWindow.xaml').read_text(encoding='utf-8')
check('using Microsoft.Playwright;' in main_code, 'Playwright IPage namespace imported')
check('await ShowNewWorkflowAsync(wizard)' in main_code and 'wizard.ShowDialog()' not in main_code,
      'New Workflow is modeless')
check('new NewWorkflowWindow();' in main_code and 'new NewWorkflowWindow { Owner = this }' not in main_code,
      'New Workflow stays independent when main window is minimized')
check('ProtocolCombo' in new_workflow_xaml and 'PortBox' in new_workflow_xaml,
      'HTTP HTTPS and custom port controls present')
check('BuildWebsiteUrl()' in new_workflow and 'IsLocalAddress(' in new_workflow,
      'local IP and website normalization implemented')
check('System.Windows.Clipboard.GetText()' in new_workflow,
      'WPF Clipboard namespace is unambiguous')
check('Stop-RunningLaunchFlow' in build_script and 'EXIT_FOR_BUILD' in app,
      'builder requests LaunchFlow shutdown through session IPC')
main_xaml=(root/'LaunchFlow/MainWindow.xaml').read_text(encoding='utf-8')
keepass_code=(root/'LaunchFlow/Services/KeePassXcService.cs').read_text(encoding='utf-8')
picker_code=(root/'LaunchFlow/KeePassEntryPickerWindow.xaml.cs').read_text(encoding='utf-8')
check('WorkflowEnabledToggle' in main_xaml and 'x:Name="EnabledCheck"' in main_xaml,
      'workflow enabled toggle retains existing binding')
check('FindDatabases()' in keepass_code and 'SearchOption.TopDirectoryOnly' in keepass_code,
      'KeePassXC database discovery uses familiar folders')
check('ListEntriesAsync' in keepass_code and 'ArgumentList.Add("ls")' in keepass_code,
      'KeePassXC login picker lists entry names')
check('ChooseKeePassEntry_Click' in main_code and 'FirstOrDefault(w => w.Id == workflowId)' in main_code,
      'selected KeePassXC login belongs to the original workflow')
check('ArgumentList.Add("show")' not in keepass_code and (root/'LaunchFlow/KeePassEntryPickerWindow.xaml').exists(),
      'entry picker does not display KeePassXC secrets')
main_xaml=(root/'LaunchFlow/MainWindow.xaml').read_text(encoding='utf-8')
styles=(root/'LaunchFlow/App.xaml').read_text(encoding='utf-8')
db=(root/'LaunchFlow/Services/VaultDatabase.cs').read_text(encoding='utf-8')
check('Header="Activity"' not in main_xaml and 'LogBox' not in main_xaml,
      'activity screen removed')
check('Local backups' not in main_xaml and 'BackupVault_Click' not in main_xaml,
      'backup controls removed')
service_sources='\n'.join(file.read_text(encoding='utf-8') for file in (root/'LaunchFlow').rglob('*.cs'))
check('VaultBackupService' not in service_sources,
      'no references to removed backup service')
check('VaultBackupService' not in main_code and 'VaultBackupService' not in db,
      'automatic database backup calls removed')
check(not (root/'LaunchFlow/Services/VaultBackupService.cs').exists(),
      'unused database backup service removed')
check('Value="#27C866"' in styles and 'Value="#27C866"' in main_xaml,
      'enabled controls use solid green')
check('ImportLaunchFlowFile' in main_code and 'SecureTransferService.Export' in main_code,
      'protected import and export remain available')
print('\n'+ ('FAILED: '+', '.join(errors) if errors else 'Source/configuration checks PASS. Native Windows build remains required.'))
sys.exit(bool(errors))
