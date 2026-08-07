#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=android-env.sh
source "$root/build/android-env.sh"

configuration="${1:-Debug}"
target="${2:-all}"
case "$configuration" in
  Debug|Release) ;;
  *) echo "Configuration must be Debug or Release." >&2; exit 2 ;;
esac

projects=()
case "$target" in
  probe|runtime-probe)
    projects+=("$root/tools/JunimoGate.RuntimeProbe/JunimoGate.RuntimeProbe.csproj")
    ;;
  app)
    projects+=("$root/src/JunimoGate.App/JunimoGate.App.csproj")
    ;;
  all)
    projects+=(
      "$root/tools/JunimoGate.RuntimeProbe/JunimoGate.RuntimeProbe.csproj"
      "$root/src/JunimoGate.App/JunimoGate.App.csproj"
    )
    ;;
  *) echo "Target must be all, app, or probe." >&2; exit 2 ;;
esac

"$root/build/report-android-environment.sh" >/dev/null
"$root/build/build-mono-android.sh"
"$root/build/build-harmony-android.sh"
"$root/build/build-cacheflush.sh"

for project in "${projects[@]}"; do
  printf '\nBuilding %s (%s)...\n' "${project#$root/}" "$configuration"
  "$DOTNET_ROOT/dotnet" build "$project" \
    --configuration "$configuration" \
    --maxcpucount:1 \
    --property:AndroidSdkDirectory="$ANDROID_SDK_ROOT" \
    --property:JavaSdkDirectory="$JAVA_HOME"
done

printf '\nGenerated ARM64 APKs:\n'
for project in "${projects[@]}"; do
  project_dir="$(dirname "$project")"
  output_dir="$project_dir/bin/$configuration/net9.0-android35.0/android-arm64"
  find "$output_dir" -maxdepth 1 -type f -name '*.apk' -print 2>/dev/null | sort || true
done
