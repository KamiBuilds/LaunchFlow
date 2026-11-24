#!/usr/bin/env python3
"""Run real source-linked storage tests; retain secret-free receipts only."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys

parser = argparse.ArgumentParser()
parser.add_argument("--label", required=True)
parser.add_argument("--dotnet", default=os.environ.get("DOTNET") or shutil.which("dotnet"))
parser.add_argument("--evidence", required=True)
parser.add_argument("--permissions", action="store_true")
parser.add_argument("--no-hooks", action="store_true")
args = parser.parse_args()
if not args.dotnet:
    parser.error("Set DOTNET or pass --dotnet for an installed .NET 8 SDK.")
if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,80}", args.label):
    parser.error("Use a simple result label without path separators.")
if os.name == "posix":
    import resource
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
if args.permissions and (not sys.platform.startswith("linux") or not shutil.which("setpriv")):
    parser.error("Permission-denial controls require Linux and setpriv.")
evidence = Path(args.evidence)
evidence.mkdir(parents=True, exist_ok=True)
root = Path(__file__).resolve().parents[2]
env = dict(os.environ)
env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
env["DOTNET_DbgEnableMiniDump"] = "0"
env["COMPlus_DbgEnableMiniDump"] = "0"
project = root / "tests/vault-creation" / ("no-hooks/HookErasure.csproj" if args.no_hooks else "VaultCreation.csproj")
commands = []
if args.permissions:
    commands.append([args.dotnet, "build", str(project), "-c", "Release", "-p:UseSharedCompilation=false"])
    commands.append(["setpriv", "--bounding-set=-dac_override,-dac_read_search", args.dotnet, str(project.parent / "bin/Release/net8.0/VaultCreation.dll"), "--permissions-only"])
else:
    commands.append([args.dotnet, "run", "--project", str(project), "-c", "Release", "-p:UseSharedCompilation=false"])
receipts = []
text = ""
for command in commands:
    try:
        result = subprocess.run(command, cwd=root, env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=240)
        output, code = result.stdout, result.returncode
    except subprocess.TimeoutExpired as error:
        output = error.stdout or ""
        if isinstance(output, bytes): output = output.decode("utf-8", errors="replace")
        output += "\nRUNNER_TIMEOUT\n"
        code = 124
    text += output + f"\nCHILD_EXIT={code}\n"
    receipts.append({"command": command, "exit": code})
    if code: break
(evidence / (args.label + ".log")).write_text(text, encoding="utf-8")
source = [*root.glob("LaunchFlow/Services/Vault/VaultDatabase*.cs"), *[p for p in (root / "tests/vault-creation").rglob("*") if p.is_file() and p.suffix in {".cs", ".csproj", ".py"} and not {"bin", "obj", "__pycache__"}.intersection(p.parts)]]
manifest = {str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(source)}
(evidence / (args.label + ".json")).write_text(json.dumps({"commands": receipts, "source_sha256": manifest, "log": args.label + ".log"}, indent=2) + "\n", encoding="utf-8")
print(text, end="")
sys.exit(receipts[-1]["exit"])
