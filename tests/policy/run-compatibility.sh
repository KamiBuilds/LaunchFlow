#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
SDK="${DOTNET:-/root/.cache/launchflow/dotnet/dotnet}"
BASELINE="c3451c26208f75f3fb57924e3dfe1db8cfc8985e"
ROOT="$(mktemp -d -t launchflow-policy-compat-XXXXXX)"
cleanup() {
  code=$?
  if [[ "$code" -eq 0 ]]; then
    rm -rf "$ROOT"
  else
    echo "Compatibility failed (exit $code); synthetic fixture retained at: $ROOT" >&2
  fi
}
trap cleanup EXIT
export XDG_DATA_HOME="$ROOT"
export LAUNCHFLOW_POLICY_TEST_ROOT="$ROOT"
if ! git cat-file -e "${BASELINE}^{commit}"; then
  echo "Pinned legacy commit $BASELINE is missing. Fetch full repository history." >&2
  exit 1
fi
git show "$BASELINE:LaunchFlow/Services/VaultDatabase.cs" > "$ROOT/BaselineVault.cs"
git show "$BASELINE:LaunchFlow/Services/ConfigStore.cs" > "$ROOT/BaselineConfigStore.cs"
git show "$BASELINE:LaunchFlow/Services/CredentialVault.cs" > "$ROOT/BaselineCredentialVault.cs"
git show "$BASELINE:LaunchFlow/Services/SecureTransferService.cs" > "$ROOT/BaselineTransfer.cs"
echo "Pinned legacy VaultDatabase: $(git rev-parse "$BASELINE:LaunchFlow/Services/VaultDatabase.cs")"
echo "Pinned legacy ConfigStore: $(git rev-parse "$BASELINE:LaunchFlow/Services/ConfigStore.cs")"
echo "Current test commit: $(git rev-parse HEAD)"
# The two projects have different source inputs and separate obj/bin directories.
# Pinned old source is never compiled together with the new ConfigStore session API.
legacy_project="tests/policy/LegacyCompatibilityTests.csproj"
legacy=(
  -c Release
  -p:VaultDatabaseSource="$ROOT/BaselineVault.cs"
  -p:LegacyConfigStoreSource="$ROOT/BaselineConfigStore.cs"
  -p:LegacyCredentialVaultSource="$ROOT/BaselineCredentialVault.cs"
  -p:LegacyTransferSource="$ROOT/BaselineTransfer.cs"
  -p:BaseIntermediateOutputPath="$ROOT/legacy-obj/"
  -p:BaseOutputPath="$ROOT/legacy-bin/"
  -p:TreatWarningsAsErrors=true
  -p:UseSharedCompilation=false
)
current=(
  --project tests/policy/PolicyTests.csproj
  -c Release
  -p:TreatWarningsAsErrors=true
  -p:UseSharedCompilation=false
)
"$SDK" build "$legacy_project" "${legacy[@]}"
legacy_dll="$ROOT/legacy-bin/Release/net8.0/LegacyCompatibilityTests.dll"
if [[ ! -f "$legacy_dll" ]]; then
  echo "Fresh legacy compatibility executable missing: $legacy_dll" >&2
  exit 1
fi
"$SDK" "$legacy_dll" --compat-create
"$SDK" run "${current[@]}" -- --compat-update
"$SDK" "$legacy_dll" --compat-read
"$SDK" build tests/policy/PolicyTests.csproj -c Release \
  -p:TreatWarningsAsErrors=true -p:UseSharedCompilation=false
echo "PASS: pinned baseline -> current session-bound save -> pinned baseline reader"
