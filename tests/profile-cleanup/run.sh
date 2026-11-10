#!/usr/bin/env bash
set -euo pipefail
ulimit -c 0
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
DOTNET="${DOTNET:-dotnet}"
"$DOTNET" build "$ROOT/tests/profile-cleanup/ProfileCleanupTests.csproj" -c Release -p:TreatWarningsAsErrors=true "$@"
DLL="$ROOT/tests/profile-cleanup/bin/Release/net8.0/ProfileCleanupTests.dll"
FIXTURE_ROOT="$(mktemp -d -t launchflow-profile-cleanup-XXXXXX)"
trap 'rm -rf "$FIXTURE_ROOT"' EXIT
cd "$FIXTURE_ROOT"
if [[ "$EUID" -eq 0 ]]; then
    capsh --drop=cap_dac_override,cap_dac_read_search -- -c 'exec "$1" "$2"' launchflow-profile-cleanup "$DOTNET" "$DLL"
else
    "$DOTNET" "$DLL"
fi
