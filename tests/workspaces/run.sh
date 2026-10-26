#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
DOTNET="${DOTNET:-dotnet}"
"$DOTNET" run --project tests/workspaces/WorkspaceTests.csproj --configuration Release
python3 tests/workspaces/extract-ui-methods.py
"$DOTNET" run --project tests/workspaces/UiTests.csproj --configuration Release
