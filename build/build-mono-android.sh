#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=android-env.sh
source "$root/build/android-env.sh"

runtime_pack_root="$DOTNET_ROOT/packs/Microsoft.NETCore.App.Runtime.Mono.android-arm64"
source_runtime="${JUNIMOGATE_MONO_RUNTIME_SOURCE:-}"
if [[ -z "$source_runtime" ]]; then
  source_runtime="$({
    find "$runtime_pack_root" -mindepth 1 -maxdepth 1 -type d -name '9.*' -print 2>/dev/null || true
  } | sort -V | tail -n 1)/runtimes/android-arm64/native/libmonosgen-2.0.so"
fi

if [[ ! -f "$source_runtime" ]]; then
  echo "The .NET 9 Android ARM64 Mono runtime was not found: $source_runtime" >&2
  exit 3
fi

output="$root/artifacts/mono-runtime/android-arm64/libmonosgen-2.0.so"
"$DOTNET_ROOT/dotnet" run \
  --project "$root/tools/JunimoGate.RuntimePack/JunimoGate.RuntimePack.csproj" \
  --configuration Release \
  -- \
  "$source_runtime" \
  "$output"
