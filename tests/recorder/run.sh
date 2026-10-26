#!/usr/bin/env bash
set -euo pipefail
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$HOME/.cache/launchflow/dotnet-home}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$HOME/.cache/launchflow/nuget}"
export PLAYWRIGHT_BROWSERS_PATH="${PLAYWRIGHT_BROWSERS_PATH:-$HOME/.cache/launchflow/recorder-browsers}"
exec "${DOTNET:-dotnet}" run --configuration Release --project "$here/RecorderTests.csproj" -p:TreatWarningsAsErrors=true "$@"
