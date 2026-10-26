#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
SDK="${DOTNET:-/root/.cache/launchflow/dotnet/dotnet}"
ROOT="$(mktemp -d -t launchflow-policy-XXXXXX)"
trap 'rm -rf "$ROOT"' EXIT
export XDG_DATA_HOME="$ROOT"
export LAUNCHFLOW_POLICY_TEST_ROOT="$ROOT"
"$SDK" run --project tests/policy/PolicyTests.csproj "$@"
