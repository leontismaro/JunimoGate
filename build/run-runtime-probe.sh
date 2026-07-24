#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=android-env.sh
source "$root/build/android-env.sh"

configuration="${1:-Debug}"
case "$configuration" in
  Debug|Release) ;;
  *) echo "Configuration must be Debug or Release." >&2; exit 2 ;;
esac

adb="$ANDROID_SDK_ROOT/platform-tools/adb"
package="org.junimogate.runtimeprobe"
component="$package/$package.MainActivity"

if [[ -n "${ANDROID_SERIAL:-}" ]]; then
  serial="$ANDROID_SERIAL"
else
  mapfile -t devices < <("$adb" devices | awk 'NR > 1 && $2 == "device" {print $1}')
  if ((${#devices[@]} != 1)); then
    printf 'Expected exactly one ready Android device, found %d. Set ANDROID_SERIAL when multiple devices are attached.\n' "${#devices[@]}" >&2
    "$adb" devices -l >&2
    exit 3
  fi
  serial="${devices[0]}"
fi
adb_device=("$adb" -s "$serial")

state="$("${adb_device[@]}" get-state 2>/dev/null || true)"
[[ "$state" == "device" ]] || { echo "Android device $serial is not ready (state: ${state:-unknown})." >&2; exit 3; }
abis="$("${adb_device[@]}" shell getprop ro.product.cpu.abilist | tr -d '\r')"
if [[ ",$abis," != *,arm64-v8a,* ]]; then
  echo "RuntimeProbe requires an ARM64 device; $serial reports: $abis" >&2
  exit 3
fi

"$root/build/build-android.sh" "$configuration" probe

project_dir="$root/tools/JunimoGate.RuntimeProbe"
output_dir="$project_dir/bin/$configuration/net9.0-android35.0/android-arm64"
apk="$output_dir/org.junimogate.runtimeprobe-Signed.apk"
[[ -f "$apk" ]] || { echo "No current ARM64 RuntimeProbe APK found at $apk." >&2; exit 4; }

printf 'Installing %s on %s...\n' "$apk" "$serial"
"${adb_device[@]}" install --no-incremental -r -t "$apk" >/dev/null
"${adb_device[@]}" shell pm clear "$package" >/dev/null
"${adb_device[@]}" logcat -c
"${adb_device[@]}" shell am start -W -n "$component"

artifact_dir="$root/artifacts/runtime-probe"
mkdir -p "$artifact_dir"
safe_serial="$(printf '%s' "$serial" | tr -c 'A-Za-z0-9._-' '_')"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
report="$artifact_dir/${safe_serial}-${configuration,,}-$stamp.json"
log="$artifact_dir/${safe_serial}-${configuration,,}-$stamp.logcat.txt"
temporary="$(mktemp)"
trap 'rm -f "$temporary"' EXIT

printf 'Waiting for app-private runtime-probe-report.json...\n'
completed=false
for _ in $(seq 1 120); do
  if "${adb_device[@]}" exec-out run-as "$package" cat files/runtime-probe-report.json >"$temporary" 2>/dev/null \
    && python3 -m json.tool "$temporary" >/dev/null 2>&1; then
    completed=true
    break
  fi
  sleep 1
done

"${adb_device[@]}" logcat -d -v threadtime >"$log"
if [[ "$completed" != true ]]; then
  echo "RuntimeProbe did not produce a valid report within 120 seconds." >&2
  echo "Logcat: $log" >&2
  exit 5
fi
mv "$temporary" "$report"
trap - EXIT

conclusion="$(python3 - "$report" <<'PY'
import json,sys
value=json.load(open(sys.argv[1]))
print(value.get('conclusion') or value.get('Conclusion') or 'missing')
PY
)"
printf 'RuntimeProbe conclusion: %s\nReport: %s\nLogcat: %s\n' "$conclusion" "$report" "$log"

case "$conclusion" in
  stock-runtime-passed|stock-runtime-passed-with-harmony-monomod-fix)
    exit 0
    ;;
  *)
    exit 1
    ;;
esac
