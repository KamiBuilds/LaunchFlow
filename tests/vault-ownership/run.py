#!/usr/bin/env python3
"""Exercise a built application DLL with disposable synthetic ownership fixtures."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--application", type=Path, required=True,
                        help="Directory containing the complete built application and dependencies")
    parser.add_argument("--results", type=Path, required=True)
    parser.add_argument("--dotnet", default=os.environ.get("DOTNET") or shutil.which("dotnet"))
    args = parser.parse_args()
    if not args.dotnet:
        parser.error("Set DOTNET or supply --dotnet for an installed .NET 8 SDK.")
    application = args.application.resolve()
    assembly = application / "LaunchFlow.dll"
    if not assembly.is_file():
        parser.error("The complete application must be built first.")
    results = args.results.resolve()
    results.mkdir(parents=True, exist_ok=True)
    project = Path(__file__).resolve().parent / "VaultOwnershipTests.csproj"
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1",
               DOTNET_DbgEnableMiniDump="0", COMPlus_DbgEnableMiniDump="0")
    if os.name == "posix":
        import resource
        resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    build = subprocess.run(
        [args.dotnet, "build", str(project), "-c", "Release",
         "-p:UseSharedCompilation=false", "-warnaserror"],
        env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        text=True, timeout=180, check=False,
    )
    (results / "host-build.log").write_text(build.stdout, encoding="utf-8")
    if build.returncode:
        print(json.dumps({"stage": "host-build", "exit_code": build.returncode}))
        return build.returncode
    started = time.monotonic()
    receipt: dict[str, object] = {
        "assembly_sha256": hashlib.sha256(assembly.read_bytes()).hexdigest(),
        "scope": "Complete application DLL methods; not native UI acceptance",
        "fixtures_removed": False,
    }
    with tempfile.TemporaryDirectory(prefix="lf-ownership-") as owned:
        fixture = Path(owned)
        home = fixture / "home"
        home.mkdir()
        corpus = fixture / "corpus"
        corpus.mkdir()
        env["XDG_DATA_HOME"] = str(home)
        for name in ("LF_AUDIT_REFERENCE", "LF_DIAG_VALUE", "LAUNCHFLOW_TEST_VAULT_PATH"):
            env.pop(name, None)
        native = application / "runtimes" / "linux-x64" / "native"
        if native.is_dir():
            env["LD_LIBRARY_PATH"] = str(native) + os.pathsep + env.get("LD_LIBRARY_PATH", "")
        try:
            run = subprocess.run(
                [args.dotnet, str(project.parent / "bin/Release/net8.0/VaultOwnershipTests.dll"),
                 str(application), str(corpus), str(results)],
                env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                text=True, timeout=900, check=False,
            )
            (results / "execution.log").write_text(run.stdout, encoding="utf-8")
            receipt["exit_code"] = run.returncode
        except subprocess.TimeoutExpired:
            receipt["exit_code"] = 124
            receipt["timed_out"] = True
        finally:
            receipt["elapsed_seconds"] = time.monotonic() - started
    receipt["fixtures_removed"] = not fixture.exists()
    (results / "execution.json").write_text(json.dumps(receipt, indent=2), encoding="utf-8")
    print(json.dumps(receipt))
    return int(receipt["exit_code"])


if __name__ == "__main__":
    raise SystemExit(main())
