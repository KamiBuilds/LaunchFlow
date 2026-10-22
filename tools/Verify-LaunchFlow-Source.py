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
check(version == '0.7.8', 'csproj version')
iss=(root/'installer/LaunchFlow.iss').read_text(encoding='utf-8')
check('#define AppVersion "'+version+'"' in iss,'installer matches project version')
check('AppId={{9B1A7C15-093B-4DBA-BDA7-BF43A2E67320}' in iss,'stable upgrade AppId')
check('VersionInfoVersion='+version+'.0' in iss,'installer Windows version')
cs=(root/'LaunchFlow/Services/DarkWindowChrome.cs').read_text(encoding='utf-8')
check('System.Windows.Media.Color.FromRgb' in cs,'WPF Color disambiguation')
app=(root/'LaunchFlow/App.xaml.cs').read_text(encoding='utf-8')
check(app.count('_ = Dispatcher.BeginInvoke(')==2,'two explicit UI dispatcher fire-and-forget calls')
auto=(root/'LaunchFlow/Services/AutomationEngine.cs').read_text(encoding='utf-8')
check('new Dictionary<string, object?>' not in auto and auto.count('new Dictionary<string, object>')>=2,'Playwright CDP argument nullability')
workflow=(root/'.github/workflows/build-windows-installer.yml').read_text(encoding='utf-8')
check('LaunchFlow-Setup-v'+version+'-x64.exe' in workflow and '0.7.3' not in workflow,'CI artifact path matches version')
check((root/'BUILD-INSTALLER.cmd').is_file() and (root/'BUILD-INSTALLER.ps1').is_file(),'installer builders included')
check((root/'LaunchFlow/Assets/LaunchFlow.ico').is_file(),'app icon present')
for xaml in (root/'LaunchFlow').glob('*.xaml'):
    try: ET.parse(xaml)
    except ET.ParseError as ex: errors.append(f'Invalid XAML XML {xaml.name}: {ex}')
check(len(list((root/'LaunchFlow').glob('*.xaml')))>=5,'XAML files parsed')
print('\n'+ ('FAILED: '+', '.join(errors) if errors else 'Source/configuration checks PASS. Native Windows build remains required.'))
sys.exit(bool(errors))
