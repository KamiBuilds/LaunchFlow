#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
SDK="${DOTNET:-/root/.cache/launchflow/dotnet/dotnet}"
BASELINE="c3451c26208f75f3fb57924e3dfe1db8cfc8985e"
ROOT="$(mktemp -d -t launchflow-policy-compat-XXXXXX)"
cleanup() {
  code=$?
  if [[ "$code" -eq 0 ]]; then rm -rf "$ROOT"
  else echo "Compatibility failed (exit $code); synthetic fixture retained at: $ROOT" >&2; fi
}
trap cleanup EXIT
export XDG_DATA_HOME="$ROOT"
export LAUNCHFLOW_POLICY_TEST_ROOT="$ROOT"
if ! git cat-file -e "${BASELINE}^{commit}"; then
  echo "Pinned legacy commit $BASELINE is missing; fetch full history." >&2
  exit 1
fi
git show "$BASELINE:LaunchFlow/Services/VaultDatabase.cs" > "$ROOT/BaselineVault.cs"
git show "$BASELINE:LaunchFlow/Services/ConfigStore.cs" > "$ROOT/BaselineConfigStore.cs"
git show "$BASELINE:LaunchFlow/Services/CredentialVault.cs" > "$ROOT/BaselineCredentialVault.cs"
git show "$BASELINE:LaunchFlow/Services/SecureTransferService.cs" > "$ROOT/BaselineTransfer.cs"
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
current=(--project tests/policy/PolicyTests.csproj -c Release -p:TreatWarningsAsErrors=true -p:UseSharedCompilation=false)
"$SDK" build tests/policy/LegacyCompatibilityTests.csproj "${legacy[@]}"
legacy_dll="$ROOT/legacy-bin/Release/net8.0/LegacyCompatibilityTests.dll"
if [[ ! -f "$legacy_dll" ]]; then
  echo "Pinned legacy executable not produced." >&2
  exit 1
fi
"$SDK" "$legacy_dll" --compat-create
"$SDK" run "${current[@]}" -- --compat-update
"$SDK" run "${current[@]}" -- --compat-verify
"$SDK" "$legacy_dll" --compat-deny
OLD_ROOT="$ROOT/recovery-old"
mkdir -p "$OLD_ROOT/LaunchFlow"
backup="$(find "$ROOT/LaunchFlow/recovery" -maxdepth 1 -name 'launchflow-legacy-*.db' -type f | head -n 1)"
if [[ -z "$backup" ]]; then echo "Legacy recovery copy missing." >&2; exit 1; fi
cp -- "$backup" "$OLD_ROOT/LaunchFlow/launchflow.db"
cp -- "$ROOT/baseline.launchflow" "$OLD_ROOT/baseline.launchflow"
export XDG_DATA_HOME="$OLD_ROOT"
export LAUNCHFLOW_POLICY_TEST_ROOT="$OLD_ROOT"
"$SDK" "$legacy_dll" --compat-read
echo "PASS: real old writer -> explicit strict upgrade -> v3 cold read, old denial and untouched legacy recovery"
