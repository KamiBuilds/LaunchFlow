from pathlib import Path
import hashlib, json, os, re
root = Path(os.environ.get("LAUNCHFLOW_SOURCE_ROOT", Path(__file__).resolve().parents[2]))
out = Path(__file__).resolve().parent / "obj"
out.mkdir(exist_ok=True)
src = (root / "LaunchFlow/App.xaml.cs").read_text(encoding="utf-8")
# Match a balanced body by its declaration; no behavior is rewritten.
def method(text, declaration):
    start = text.index(declaration)
    brace = text.index("{", start)
    depth = 1
    end = brace + 1
    while depth:
        depth += (text[end] == "{") - (text[end] == "}")
        end += 1
    return text[start:end]
marker = 'else if (string.Equals(message, "EXIT_FOR_INSTALL"'
callback = src[src.index(marker):]
start = callback.index("new Action(() =>") + len("new Action(() =>")
end = callback.index("}));", start) + 1
body = callback[start:end]
parts = ["#nullable enable\nusing System.Windows;\nusing System.IO.Pipes;\nusing System.Text;\nusing System.Diagnostics;\nusing LaunchFlow.Services;\nusing LaunchFlow.Models;\nnamespace LaunchFlow;\npublic partial class App { public void ExitForTest() " + body + "\n}"]
methods = [method(src, decl) for decl in ["    private static bool InitializeVault()", "    private static bool TryRecoverIncompleteVault()", "    internal void ContinueVaultResetSetup()", "    private async Task ListenForActivationAsync(CancellationToken cancellation)"]]
keepalive_start = src.index("    internal void KeepAliveForVaultResetSetup()")
methods.append(src[keepalive_start:src.index(";", keepalive_start)+1])
methods.append(method(src,"    internal void CancelVaultResetKeepAlive()"))
startup = src[src.index("        try\n        {\n            // Verify external"):src.index("\n    internal static string? FindOpenablePath")]
# Exclude only the enclosing OnStartup closing brace, retaining its try/catch unchanged.
startup = startup.rsplit("\n    }", 1)[0]
parts.append("public partial class App {\n" + "\n".join(methods) + "\npublic void StartupForTest() { bool startInTray=false;\n" + startup + "\n}\n}")
recovery = (root / "LaunchFlow/App.VaultRecovery.cs").read_text(encoding="utf-8")
parts.append("public partial class App {\n" + method(recovery, "    internal static bool TryStartFreshVault(VaultDatabase db)") + "\n}")
main = (root / "LaunchFlow/UI/Shell/MainWindow.xaml.cs").read_text(encoding="utf-8")
parts.append("public partial class MainWindow {\n" + method(main, "    internal async Task RequestShutdownForInstallAsync()") + "\n}")
reset_path = root / "LaunchFlow/UI/Shell/MainWindow.PasswordManager.cs"
parts.append("public partial class MainWindow {\n" + method(reset_path.read_text(encoding="utf-8"), "    private async Task ContinueToSetupAfterVaultResetAsync()") + "\n}")
partial = root / "LaunchFlow/App.StartupExit.cs"
if partial.exists():
    parts.append(partial.read_text(encoding="utf-8").replace("using System.Windows;", "").replace("namespace LaunchFlow;", ""))
(out / "Production.g.cs").write_text("\n".join(parts), encoding="utf-8")
manifest={"source_root":str(root),"callback_sha256":hashlib.sha256(body.encode()).hexdigest(),"files":{str(p.relative_to(root)):hashlib.sha256(p.read_bytes()).hexdigest() for p in [root/"LaunchFlow/App.xaml.cs",root/"LaunchFlow/UI/Shell/MainWindow.xaml.cs",reset_path,root/"LaunchFlow/App.VaultRecovery.cs",partial] if p.exists()}}
(out / "source-binding.json").write_text(json.dumps(manifest,indent=2)+"\n",encoding="utf-8")
print(json.dumps(manifest))
