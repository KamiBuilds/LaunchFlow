#!/usr/bin/env python3
"""Compile unchanged MainWindow method bodies against explicit portable UI/engine seams."""
import pathlib
import re

root = pathlib.Path(__file__).resolve().parents[2]
source = "\n".join(path.read_text() for path in sorted((root / "LaunchFlow").glob("MainWindow*.cs")))
names = ["RunEnabledAsync", "RequestCrashCleanup", "RecordSteps_Click", "SecureVaultLockAsync", "SecureVaultLockCoreAsync", "UpdateRunButtons", "RequireMasterPassword", "SaveTaughtStepsAsync", "ChooseKeePassEntry_Click", "DescribeCredentialStatus", "NeedsKeePassForRun", "EnsureKeePassPasswordForRun", "LoadCredentialEditor", "SaveCredentials_Click", "ConfirmLoginOriginAsync", "AutomationAllowed", "TeachAllowed", "CredentialReadAllowed", "LoginModeIndex", "SelectedLoginMode", "QueueWorkflowStatus", "OfferRepairForLoginAttentionAsync", "OfferRepairForStepAttention", "RepairLoginCoreAsync", "OnWindowsLocked", "OnWindowsUnlockedAsync", "RunAfterUnlockAsync", "LockAfterThresholdAsync"]
methods = []
for name in names:
    match = re.search(r"^    (?:private|internal) (?:async )?[^\n]+\b" + name + r"\([^)]*\)\s*\n    \{", source, re.M)
    if match:
        end = source.index("\n    }", match.end()) + len("\n    }")
        methods.append(source[match.start():end])
    else:
        raise RuntimeError("Production method missing or unsupported signature: " + name)
output = """// Generated from current production MainWindow. Do not edit.
#nullable enable
using System.IO;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;
using Forms = System.Windows.Forms;
partial class UiFixture
{
    public Task Run(bool selected = false, WorkspaceProfile? workspace = null) => RunEnabledAsync(selected, workspace);
    public Task Lock(string message = "test") => SecureVaultLockAsync(message);
    public Task Teach(WebsiteWorkflow workflow) => SaveTaughtStepsAsync(workflow, new FakePage());
    public bool Unlock(bool forcePrompt = true) => RequireMasterPassword(forcePrompt);
    public string Status(WebsiteWorkflow w) => DescribeCredentialStatus(w);
    public bool NeedsKeePass(WebsiteWorkflow w) => NeedsKeePassForRun(w);
    public void Load(WebsiteWorkflow w) => LoadCredentialEditor(w);
    public void SaveCredential() => SaveCredentials_Click(this, new RoutedEventArgs());
    public Task<bool> Approve(WebsiteWorkflow w, string origin) => ConfirmLoginOriginAsync(w, origin);
    public Task Repair() => RepairLoginCoreAsync();
    public void WindowsLock() => OnWindowsLocked();
    public Task WindowsUnlock() => OnWindowsUnlockedAsync();
    public Task AfterUnlock() => RunAfterUnlockAsync();
METHODS
}
""".replace("METHODS", "\n".join(methods))
target = root / "tests/workspaces/obj/UiMethods.g.cs"
target.parent.mkdir(parents=True, exist_ok=True)
app = (root / "LaunchFlow/App.xaml.cs").read_text()
callbacks = []
for event, wrapper in [("DispatcherUnhandledException", "DispatcherFailure"), ("AppDomain.CurrentDomain.UnhandledException", "FatalFailure")]:
    marker = event + " += (_, args) =>\n        {"
    start = app.index(marker) + len(marker)
    end = app.index("\n        };", start)
    callbacks.append("    public void " + wrapper + "(FailureArgs args)\n    {" + app[start:end] + "\n    }")
output += "\npartial class CrashFixture\n{\n" + "\n".join(callbacks) + "\n}\n"
target.write_text(output)
print(f"Extracted {len(methods)} production methods and {len(callbacks)} App crash callbacks: " + ", ".join(names))
