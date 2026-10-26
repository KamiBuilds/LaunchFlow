#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
SDK="${DOTNET:-/root/.cache/launchflow/dotnet/dotnet}"
ROOT="$(mktemp -d -t launchflow-policy-compat-XXXXXX)"
trap 'rm -rf "$ROOT"' EXIT
export XDG_DATA_HOME="$ROOT"
export LAUNCHFLOW_POLICY_TEST_ROOT="$ROOT"
git show c3451c2:LaunchFlow/Services/VaultDatabase.cs > "$ROOT/BaselineVault.cs"
"$SDK" run --project tests/policy/PolicyTests.csproj -p:VaultDatabaseSource="$ROOT/BaselineVault.cs" -- --compat-create
"$SDK" run --project tests/policy/PolicyTests.csproj -- --compat-update
"$SDK" run --project tests/policy/PolicyTests.csproj -p:VaultDatabaseSource="$ROOT/BaselineVault.cs" -- --compat-read
# Restore the harness build to the actual production source, not the baseline override.
"$SDK" build tests/policy/PolicyTests.csproj --no-restore -warnaserror
