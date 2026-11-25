#!/usr/bin/env python3
"""Bounded local preparation tests. Never claims Windows installation/UI execution."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

p = argparse.ArgumentParser()
p.add_argument('--pwsh', required=True)
p.add_argument('--dotnet', required=True)
p.add_argument('--evidence', type=Path, required=True)
a = p.parse_args()
root = Path(__file__).resolve().parent
repo = root.parent.parent
a.evidence.mkdir(parents=True, exist_ok=True)
commands = [(name, [a.pwsh, '-NoLogo', '-NoProfile', '-File', str(root / name)]) for name in
            ('Test-Ast.ps1', 'Test-Mechanisms.ps1', 'Test-JsonGuard.ps1', 'Test-Ownership.ps1', 'Test-Package.ps1')]
commands += [('portable-csharp', [a.dotnet, 'run', '--project', str(root / 'MechanismTests.csproj'), '-c', 'Release', '-p:UseSharedCompilation=false']),
             ('native-csharp-compile-only', [a.dotnet, 'build', str(root / 'NativeCompile.csproj'), '-c', 'Release', '-p:UseSharedCompilation=false'])]
# Preserve the original seven gates; these focused regressions add portable coverage.
commands += [(name, [a.pwsh, '-NoLogo', '-NoProfile', '-File', str(root / name)]) for name in
             ('Test-Registration.ps1', 'Test-RecoveryOracle.ps1', 'Test-BuildOwnership.ps1', 'Test-JsonShape.ps1')]
results = []
for name, command in commands:
    try:
        r = subprocess.run(command, cwd=repo, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)
        code, output = r.returncode, r.stdout
    except subprocess.TimeoutExpired:
        code, output = 124, 'TIMEOUT bounded portable check\n'
    log = a.evidence / (name + '.log')
    log.write_text(output + '\nEXIT=' + str(code) + '\n', encoding='utf-8')
    results.append({'name': name, 'exit': code, 'log': log.name, 'logSha256': hashlib.sha256(log.read_bytes()).hexdigest()})
    print(name + ': ' + str(code), flush=True)
result = {'schema': 1, 'platform': os.name, 'nativeWindowsExecuted': False, 'results': results}
(a.evidence / 'portable-results.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
raise SystemExit(0 if all(r['exit'] == 0 for r in results) else 1)
