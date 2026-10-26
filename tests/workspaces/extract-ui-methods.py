#!/usr/bin/env python3
"""Compile unchanged MainWindow method bodies against explicit portable UI/engine seams."""
import pathlib
import re

root = pathlib.Path(__file__).resolve().parents[2]
source = (root / "LaunchFlow/MainWindow.xaml.cs").read_text()
names = ["RunEnabledAsync", "SecureVaultLock", "SecureVaultLockAsync", "SecureVaultLockCoreAsync", "UpdateRunButtons", "RequireMasterPassword", "SaveTaughtStepsAsync"]
methods = []
for name in names:
    match = re.search(r"^    private (?:async )?[^\n]+\b" + name + r"\([^\n]*\)\s*\n    \{", source, re.M)
    if match:
        end = source.index("\n    }", match.end()) + len("\n    }")
        methods.append(source[match.start():end])
lock = "SecureVaultLockAsync(message)" if "private Task SecureVaultLockAsync" in source or "private async Task SecureVaultLockAsync" in source else "LegacyLock(message)"
legacy = "private Task LegacyLock(string message) { SecureVaultLock(message); return Task.CompletedTask; }" if lock.startswith("Legacy") else ""
workspace = ", workspace" if re.search(r"RunEnabledAsync\(bool onlySelected = false, WorkspaceProfile\? workspace", source) else ""
output = """// Generated from current production MainWindow. Do not edit.
#nullable enable
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;
using Forms = System.Windows.Forms;
partial class UiFixture
{
    public Task Run(bool selected = false, WorkspaceProfile? workspace = null) => RunEnabledAsync(selectedWORKSPACE);
    public Task Lock(string message = "test") => LOCK;
    public Task Teach(WebsiteWorkflow workflow) => SaveTaughtStepsAsync(workflow, new FakePage());
    public bool Unlock(bool forcePrompt = true) => RequireMasterPassword(forcePrompt);
LEGACY
METHODS
}
""".replace("WORKSPACE", workspace).replace("LOCK", lock).replace("LEGACY", legacy).replace("METHODS", "\n".join(methods))
target = root / "tests/workspaces/obj/UiMethods.g.cs"
target.parent.mkdir(parents=True, exist_ok=True)
target.write_text(output)
print("Extracted production methods: " + ", ".join(re.findall(r"private (?:async )?[^\n]+\b(\w+)\([^\n]*\)\s*\n    \{", "\n".join(methods))))
