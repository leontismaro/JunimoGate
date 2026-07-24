#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=android-env.sh
source "$root/build/android-env.sh"

proxy_url="${JUNIMOGATE_PROXY_URL:-}"
if [[ -n "$proxy_url" ]]; then
  export HTTP_PROXY="$proxy_url"
  export HTTPS_PROXY="$proxy_url"
  export ALL_PROXY="$proxy_url"
  export http_proxy="$proxy_url"
  export https_proxy="$proxy_url"
  export all_proxy="$proxy_url"
  printf 'Checking proxy %s before workload installation...\n' "$proxy_url"
  curl --fail --silent --show-error --head --max-time 15 \
    --proxy "$proxy_url" https://api.nuget.org/v3/index.json >/dev/null
fi

mkdir -p "$NUGET_PACKAGES" "$NUGET_HTTP_CACHE_PATH"
workload_temp="$JUNIMOGATE_TOOLCHAINS_DIR/workload-temp"
mkdir -p "$workload_temp"
chmod 700 "$workload_temp"

current="$($DOTNET_ROOT/dotnet workload list)"
if grep -Eq '^[[:space:]]*android[[:space:]]' <<<"$current"; then
  printf 'Android workload is already installed in %s.\n' "$DOTNET_ROOT"
  exit 0
fi

printf 'Installing only the Android workload into %s.\n' "$DOTNET_ROOT"
printf 'Parallel NuGet download is disabled; temp/cache paths are project-local.\n'
"$DOTNET_ROOT/dotnet" workload install android \
  --skip-manifest-update \
  --disable-parallel \
  --temp-dir "$workload_temp" \
  --source https://api.nuget.org/v3/index.json

"$DOTNET_ROOT/dotnet" workload list
