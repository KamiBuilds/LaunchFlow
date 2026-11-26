"""Isolated source-linked inspection tests; optional source-root baseline replay."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--dotnet', required=True)
parser.add_argument('--source', type=Path)
args = parser.parse_args()
if os.name != 'nt':
    import resource
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
project = Path(__file__).with_name('VaultInspectionTests.csproj').resolve()
with tempfile.TemporaryDirectory(prefix='lfi-') as directory:
    root = Path(directory)
    (root / 'home' / 'LaunchFlow').mkdir(parents=True)
    (root / 'tmp').mkdir()
    env = {k: v for k, v in os.environ.items() if not k.startswith(('LAUNCHFLOW_', 'LF_DIAG_'))}
    env.update(TMPDIR=str(root / 'tmp'), TEMP=str(root / 'tmp'), TMP=str(root / 'tmp'),
               XDG_DATA_HOME=str(root / 'home'), LAUNCHFLOW_TEST_VAULT_PATH=str(root / 'tmp' / 'singleton.db'),
               DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1',
               DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER='1', MSBUILDDISABLENODEREUSE='1')
    command = [args.dotnet, 'build', str(project), '-c', 'Release', '--no-incremental', '-p:UseSharedCompilation=false']
    if args.source:
        command.append('-p:LaunchFlowSourceRoot=' + str(args.source.resolve(strict=True)))
    code = subprocess.run(command, env=env, timeout=180).returncode
    if code == 0:
        code = subprocess.run([args.dotnet, str(project.parent / 'bin/Release/net8.0/VaultInspectionTests.dll')],
                              env=env, timeout=240).returncode
raise SystemExit(code)
