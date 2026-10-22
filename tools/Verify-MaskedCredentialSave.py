#!/usr/bin/env python3
"""Static regression checks for v0.7.8. A Windows build is still required."""
from pathlib import Path
import xml.etree.ElementTree as ET
import re
root=Path(__file__).resolve().parent.parent
ui=root/'LaunchFlow/MainWindow.xaml'
new_ui=root/'LaunchFlow/NewWorkflowWindow.xaml'
code=root/'LaunchFlow/MainWindow.xaml.cs'
new_code=root/'LaunchFlow/NewWorkflowWindow.xaml.cs'
for f in [ui,new_ui]: ET.parse(f)
ns={'w':'http://schemas.microsoft.com/winfx/2006/xaml/presentation','x':'http://schemas.microsoft.com/winfx/2006/xaml'}
def assert_pass(ok,description):
    if not ok: raise AssertionError(description)
    print('[OK]',description)
for f in [ui,new_ui]:
    el=ET.parse(f).getroot()
    all_controls=[(c.tag.rsplit('}',1)[-1],c.attrib.get('{http://schemas.microsoft.com/winfx/2006/xaml}Name')) for c in el.iter()]
    matches=[kind for kind,name in all_controls if name=='UsernameBox']
    assert_pass(matches==['PasswordBox'],f'{f.name}: email field always masked')
    assert_pass(not any('show password' in (v or '').lower() or 'show username' in (v or '').lower() for node in el.iter() for v in node.attrib.values()),f'{f.name}: no reveal control')
s=ui.read_text()
c=code.read_text()
assert_pass('Click="SaveCredentials_Click"' in s, 'explicit Save credentials button exists')
assert_pass('private void SaveCredentials_Click(' in c, 'save event handler implemented')
assert_pass('_vault.Save(_selected.Id, new SiteCredential(username, password));' in c, 'explicit update in encrypted vault')
assert_pass('string.IsNullOrEmpty(newUsername) ? existing?.Username' in c and 'string.IsNullOrEmpty(newPassword) ? existing?.Password' in c, 'blank fields retain existing values')
assert_pass('UsernameBox.Password.Trim()' in c,'masked username accessed via Password property')
assert_pass('UsernameBox.Text' not in c,'no plaintext username textbox use')
assert_pass('_vault.Save(w.Id' not in c,'no implicit password saving on other actions')
assert_pass('UsernameBox.Password' in new_code.read_text(),'new workflow masks email')
for f in (root/'LaunchFlow').rglob('*.xaml'):
    ET.parse(f)
print('Masked credentials checks PASS (Windows build still required).')
