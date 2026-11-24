#!/usr/bin/env python3
"""Freeze the storage-only overlay; run maintained gates in an owned copy."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path

import shutil
import subprocess
import sys
import tarfile
import tempfile
import time

parser = argparse.ArgumentParser()
parser.add_argument("--evidence", required=True)
parser.add_argument("--dotnet", default=os.environ.get("DOTNET") or shutil.which("dotnet"))
args = parser.parse_args()
if not args.dotnet:
    parser.error("Set DOTNET or pass --dotnet for an installed .NET 8 SDK.")
if not sys.platform.startswith("linux") or not shutil.which("bash"):
    parser.error("The maintained shell gate runner requires Linux and bash; use the native workflow on Windows.")
import resource
resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
root = Path(__file__).resolve().parents[2]
evidence = Path(args.evidence).resolve()
evidence.mkdir(parents=True, exist_ok=False)
frozen, execution = evidence / "source", evidence / "execution"
frozen.mkdir()
base = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
archive = evidence / "base.tar"
with archive.open("wb") as stream:
    subprocess.run(["git", "archive", base], cwd=root, stdout=stream, check=True)
with tarfile.open(archive) as tar: tar.extractall(frozen, filter="data")
owned = [root / "LaunchFlow/Services/Vault/VaultDatabase.cs", root / "LaunchFlow/Services/Vault/VaultDatabase.Strict.cs"]
owned.extend(p for p in (root / "tests/vault-creation").rglob("*") if p.is_file() and p.suffix in {".py", ".cs", ".csproj"} and not {"bin", "obj", "__pycache__"}.intersection(p.parts))
for p in owned:
    target = frozen / p.relative_to(root)
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(p, target)
def manifest(folder):
    return {str(p.relative_to(folder)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(folder.rglob("*")) if p.is_file()}
frozen_manifest = manifest(frozen)
(evidence / "source-manifest.json").write_text(json.dumps(frozen_manifest, indent=2) + "\n")
shutil.copytree(frozen, execution)
sdk = args.dotnet
commands = [
    ("architecture", [sys.executable, "tests/architecture/check_partials.py"]),
    ("save", [sdk, "run", "--project", "tests/save/EncryptedSaveTests.csproj", "-c", "Release", "-p:UseSharedCompilation=false"]),
    ("storage-integrity", [sdk, "run", "--project", "tests/storage-integrity/StorageIntegrity.csproj", "-c", "Release", "-p:UseSharedCompilation=false"]),
    ("policy", ["bash", "tests/policy/run.sh", "-c", "Release", "-p:UseSharedCompilation=false"]),
    ("service-contracts", [sdk, "run", "--project", "tests/architecture/ServiceContracts.csproj", "-c", "Release", "-p:UseSharedCompilation=false"]),
    ("windows-cross-build", [sdk, "build", "LaunchFlow/LaunchFlow.csproj", "-c", "Release", "-p:EnableWindowsTargeting=true", "-p:PublishReadyToRun=false", "-p:UseSharedCompilation=false"]),
]
def run(item):
    name, command = item
    with tempfile.TemporaryDirectory(prefix="lf-storage-gate-") as directory:
        temp = Path(directory); (temp / "xdg").mkdir()
        env = dict(os.environ, TMPDIR=str(temp), XDG_DATA_HOME=str(temp / "xdg"), DOTNET=sdk, DOTNET_CLI_TELEMETRY_OPTOUT="1")
        if name == "policy": env.pop("LAUNCHFLOW_TEST_VAULT_PATH", None)
        else: env["LAUNCHFLOW_TEST_VAULT_PATH"] = str(temp / "singleton.db")
        started = time.monotonic()
        try:
            child = subprocess.run(command, cwd=execution, env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=300)
            code, output = child.returncode, child.stdout
        except subprocess.TimeoutExpired as error:
            code, output = 124, error.stdout or ""
            if isinstance(output, bytes): output = output.decode("utf-8", errors="replace")
            output += "\nRUNNER_TIMEOUT\n"
        (evidence / (name + ".log")).write_text(output + f"\nCHILD_EXIT={code}\n", encoding="utf-8")
        receipt = {"name": name, "command": command, "cwd": str(execution), "exit": code, "seconds": round(time.monotonic() - started, 3), "literal_pass_lines": sum(line.startswith("PASS") for line in output.splitlines()), "result_lines": [line for line in output.splitlines() if line.startswith("RESULT")], "log": name + ".log"}
        (evidence / (name + ".json")).write_text(json.dumps(receipt, indent=2) + "\n")
        print(json.dumps(receipt), flush=True)
        return receipt
with ThreadPoolExecutor(max_workers=2) as pool: receipts = list(pool.map(run, commands))
unchanged = manifest(frozen) == frozen_manifest
result = {"base": base, "commands": receipts, "frozen_source_unchanged": unchanged, "owned_sha256": {str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(owned)}}
(evidence / "outcomes.json").write_text(json.dumps(result, indent=2) + "\n")
print("FROZEN_SOURCE_UNCHANGED=" + str(unchanged))
raise SystemExit(0 if unchanged and all(r["exit"] == 0 for r in receipts) else 1)
