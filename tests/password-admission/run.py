"""Run source-linked admission/legacy regressions in owned temporary data."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile


def run_child(command, environment):
    process = subprocess.Popen(command, env=environment)
    try:
        return process.wait(timeout=240)
    except (subprocess.TimeoutExpired, KeyboardInterrupt):
        process.kill()
        process.wait()
        return 124


parser = argparse.ArgumentParser()
parser.add_argument("--dotnet", default=shutil.which("dotnet"))
parser.add_argument("--source", type=Path)
args = parser.parse_args()
if not args.dotnet:
    parser.error("A .NET 8 SDK is required for these developer tests.")
project = Path(__file__).resolve().parent / "PasswordAdmissionTests.csproj"
command = [args.dotnet, "build", str(project), "-c", "Release", "--no-incremental",
           "-p:UseSharedCompilation=false"]
if args.source:
    command.append("-p:LaunchFlowSourceRoot=" + str(args.source.resolve(strict=True)))
with tempfile.TemporaryDirectory(prefix="launchflow-admission-") as directory:
    root = Path(directory)
    temporary = root / "tmp"
    home = root / "home"
    temporary.mkdir()
    (home / "LaunchFlow").mkdir(parents=True)
    environment = {key: value for key, value in os.environ.items()
                   if not key.startswith(("LAUNCHFLOW_", "LF_DIAG_"))}
    environment.update({"TMPDIR": str(temporary), "TEMP": str(temporary), "TMP": str(temporary),
                        "XDG_DATA_HOME": str(home), "LAUNCHFLOW_FIXTURE_DATA_ROOT": str(root),
                        "LAUNCHFLOW_TEST_VAULT_PATH": str(temporary / "singleton.db"),
                        "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1",
                        "DOTNET_DbgEnableMiniDump": "0", "COMPlus_DbgEnableMiniDump": "0",
                        "DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER": "1", "MSBUILDDISABLENODEREUSE": "1"})
    code = run_child(command, environment)
    if code == 0:
        assembly = project.parent / "bin" / "Release" / "net8.0" / "PasswordAdmissionTests.dll"
        code = run_child([args.dotnet, str(assembly)], environment)
sys.exit(code)
