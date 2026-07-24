#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=android-env.sh
source "$root/build/android-env.sh"

adb="$ANDROID_SDK_ROOT/platform-tools/adb"
package="org.junimogate.app"
component="$package/$package.MainActivity"
play_package="com.chucklefish.stardewvalley"
timeout_seconds="${JUNIMOGATE_WORKSPACE_TIMEOUT_SECONDS:-1200}"
interrupt_timeout_seconds="${JUNIMOGATE_WORKSPACE_INTERRUPT_TIMEOUT_SECONDS:-300}"
if [[ ! "$timeout_seconds" =~ ^[0-9]+$ ]] || ((timeout_seconds < 600)); then
  echo "JUNIMOGATE_WORKSPACE_TIMEOUT_SECONDS must be an integer of at least 600." >&2
  exit 2
fi
if [[ ! "$interrupt_timeout_seconds" =~ ^[0-9]+$ ]] || ((interrupt_timeout_seconds < 60)); then
  echo "JUNIMOGATE_WORKSPACE_INTERRUPT_TIMEOUT_SECONDS must be an integer of at least 60." >&2
  exit 2
fi

if [[ -n "${ANDROID_SERIAL:-}" ]]; then
  serial="$ANDROID_SERIAL"
else
  mapfile -t devices < <("$adb" devices | awk 'NR > 1 && $2 == "device" {print $1}')
  if ((${#devices[@]} != 1)); then
    printf 'Expected exactly one ready Android device, found %d. Set ANDROID_SERIAL when multiple devices are attached.\n' "${#devices[@]}" >&2
    exit 3
  fi
  serial="${devices[0]}"
fi
adb_device=("$adb" -s "$serial")

state="$("${adb_device[@]}" get-state 2>/dev/null || true)"
[[ "$state" == "device" ]] || { echo "The selected Android device is not ready (state: ${state:-unknown})." >&2; exit 3; }
model="$("${adb_device[@]}" shell getprop ro.product.model | tr -d '\r\n')"
api="$("${adb_device[@]}" shell getprop ro.build.version.sdk | tr -d '\r\n')"
abis="$("${adb_device[@]}" shell getprop ro.product.cpu.abilist | tr -d '\r\n')"
if [[ ! "$api" =~ ^[0-9]+$ ]] || ((api < 26)); then
  echo "JunimoGate requires Android API 26 or newer; selected device reports API: ${api:-unknown}" >&2
  exit 3
fi
if [[ ",$abis," != *,arm64-v8a,* ]]; then
  echo "JunimoGate.App requires an ARM64 device; selected device reports ABI list: $abis" >&2
  exit 3
fi
printf 'Device: model=%s API=%s ABI=%s\n' "${model:-unknown}" "$api" "$abis"

"$root/build/build-android.sh" Debug app
output_dir="$root/src/JunimoGate.App/bin/Debug/net9.0-android35.0/android-arm64"
apk="$output_dir/org.junimogate.app-Signed.apk"
[[ -f "$apk" ]] || { echo "No current signed ARM64 Debug APK found at $apk." >&2; exit 4; }

printf 'Installing signed JunimoGate App (Debug)...\n'
"${adb_device[@]}" install --no-incremental -r -t "$apk" >/dev/null
"${adb_device[@]}" shell am force-stop "$package"
"${adb_device[@]}" shell pm clear "$package" >/dev/null
if ! "${adb_device[@]}" shell run-as "$package" true >/dev/null 2>&1; then
  echo "The installed Debug App does not permit run-as access to its app-private files." >&2
  exit 5
fi

work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT
first_report="$work_dir/first-report.json"
second_report="$work_dir/second-report.json"
first_state="$work_dir/first-state.json"
second_state="$work_dir/second-state.json"
source_manifest="$work_dir/source-manifest.json"
extraction_manifest="$work_dir/extraction-manifest.json"
rewrite_manifest="$work_dir/rewrite-manifest.json"
hash_listing="$work_dir/workspace-hashes.txt"
logcat_file="$work_dir/logcat.txt"

wait_for_report() {
  local destination="$1"
  local started=$SECONDS
  local partial="$destination.partial"
  while ((SECONDS - started < timeout_seconds)); do
    if "${adb_device[@]}" exec-out run-as "$package" cat files/reports/game-workspace-latest.json >"$partial" 2>/dev/null \
      && python3 -m json.tool "$partial" >/dev/null 2>&1; then
      mv "$partial" "$destination"
      return 0
    fi
    sleep 1
  done
  rm -f "$partial"
  return 1
}

wait_for_pid() {
  local started=$SECONDS
  local pid
  while ((SECONDS - started < 30)); do
    pid="$("${adb_device[@]}" shell pidof "$package" 2>/dev/null | tr -d '\r\n' || true)"
    if [[ "$pid" =~ ^[0-9]+$ ]]; then
      printf '%s\n' "$pid"
      return 0
    fi
    sleep 1
  done
  return 1
}

wait_for_staging_payload() {
  local started=$SECONDS
  local listing
  while ((SECONDS - started < interrupt_timeout_seconds)); do
    listing="$("${adb_device[@]}" shell run-as "$package" find files/runtime/staging -type f 2>/dev/null | tr -d '\r' || true)"
    if grep -Eq '^files/runtime/staging/[0-9a-f]{64}-[0-9a-f]{32}/(Content|assemblies)/' <<<"$listing"; then
      return 0
    fi
    sleep 0.25
  done
  return 1
}

read_private_json() {
  local private_path="$1"
  local destination="$2"
  "${adb_device[@]}" exec-out run-as "$package" cat "$private_path" >"$destination"
  python3 -m json.tool "$destination" >/dev/null
}

validate_report() {
  local report="$1"
  local expected_status="$2"
  local expected_key="${3:-}"
  local expected_final_bytes="${4:-}"
  python3 - "$report" "$expected_status" "$expected_key" "$expected_final_bytes" <<'PY'
import datetime
import json
import re
import sys

REPORT, EXPECTED_STATUS, EXPECTED_KEY, EXPECTED_FINAL_BYTES = sys.argv[1:]
PLAY_PACKAGE = "com.chucklefish.stardewvalley"
HEX64 = re.compile(r"^[0-9a-f]{64}$")
WINDOWS_ABSOLUTE = re.compile(r"^[A-Za-z]:[\\/]")
FORBIDDEN_KEYS = {
    "path", "sourcepath", "workspacepath", "apkpath",
    "deviceid", "deviceidentifier", "androidid", "serial", "serialnumber", "imei",
}
ALLOWED_STATUSES = {"Built", "CacheHit", "Blocked", "Failed", "Cancelled", "NotAvailable"}


def fail(message):
    raise SystemExit(f"Invalid game workspace report: {message}")


def require(condition, message):
    if not condition:
        fail(message)


def normalized_key(value):
    return re.sub(r"[^a-z0-9]", "", value.casefold())


def reject_sensitive(value, location="$"):
    if isinstance(value, dict):
        for key, child in value.items():
            require(isinstance(key, str), f"{location} contains a non-string key")
            require(normalized_key(key) not in FORBIDDEN_KEYS, f"{location} contains forbidden key {key!r}")
            reject_sensitive(child, f"{location}.{key}")
    elif isinstance(value, list):
        for index, child in enumerate(value):
            reject_sensitive(child, f"{location}[{index}]")
    elif isinstance(value, str):
        require(not value.startswith("/"), f"{location} contains an absolute Unix path")
        require(WINDOWS_ABSOLUTE.match(value) is None, f"{location} contains an absolute Windows path")
        require("/data/" not in value and "/storage/" not in value and "/sdcard/" not in value,
                f"{location} contains a device path")


def require_timestamp(value, location):
    require(isinstance(value, str) and value.strip(), f"{location} must be a timestamp string")
    try:
        datetime.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        fail(f"{location} is not an ISO-8601 timestamp")


def require_order(stages, required):
    positions = [stages.index(stage) for stage in required]
    require(positions == sorted(positions), "progressStages key stages are out of order")


with open(REPORT, "r", encoding="utf-8") as stream:
    document = json.load(stream)

require(isinstance(document, dict), "root must be an object")
require(set(document) == {
    "formatVersion", "generatedAtUtc", "packageName", "status",
    "workspaceKey", "statistics", "metrics", "progressStages", "diagnostics",
}, "root fields are incomplete or contain unexpected data")
reject_sensitive(document)
require(document["formatVersion"] == 2, "formatVersion must equal 2")
require_timestamp(document["generatedAtUtc"], "$.generatedAtUtc")
require(document["packageName"] == PLAY_PACKAGE, "packageName must identify the Google Play package")
require(document["status"] in ALLOWED_STATUSES, "status is not recognized")
require(document["status"] == EXPECTED_STATUS, f"status must be {EXPECTED_STATUS}")
key = document["workspaceKey"]
require(isinstance(key, str) and HEX64.fullmatch(key), "workspaceKey must be 64 lowercase hexadecimal characters")
if EXPECTED_KEY:
    require(key == EXPECTED_KEY, "workspaceKey changed between runs")

statistics = document["statistics"]
require(isinstance(statistics, dict), "statistics must be an object")
require(set(statistics) == {"contentFileCount", "contentBytes", "assemblyFileCount", "assemblyBytes"},
        "statistics fields are incomplete or unexpected")
for field in ("contentFileCount", "contentBytes", "assemblyFileCount", "assemblyBytes"):
    value = statistics[field]
    require(isinstance(value, int) and not isinstance(value, bool) and value > 0,
            f"statistics.{field} must be a positive integer")
require(statistics["assemblyFileCount"] >= 2, "assemblyFileCount must be at least two")
payload_bytes = statistics["contentBytes"] + statistics["assemblyBytes"]

metrics = document["metrics"]
require(isinstance(metrics, dict), "metrics must be an object for successful preparation")
require(set(metrics) == {"durationMilliseconds", "peakTemporaryBytes", "finalWorkspaceBytes"},
        "metrics fields are incomplete or unexpected")
for field in metrics:
    require(isinstance(metrics[field], int) and not isinstance(metrics[field], bool),
            f"metrics.{field} must be an integer")
require(metrics["durationMilliseconds"] > 0, "metrics.durationMilliseconds must be positive")
require(metrics["finalWorkspaceBytes"] > payload_bytes,
        "metrics.finalWorkspaceBytes must include payload and all manifests")
if EXPECTED_STATUS == "Built":
    require(metrics["peakTemporaryBytes"] > 0, "Built peakTemporaryBytes must be positive")
    require(metrics["peakTemporaryBytes"] == metrics["finalWorkspaceBytes"],
            "Built peakTemporaryBytes must equal the validated staging file total")
else:
    require(metrics["peakTemporaryBytes"] == 0, "CacheHit peakTemporaryBytes must be zero")
if EXPECTED_FINAL_BYTES:
    require(metrics["finalWorkspaceBytes"] == int(EXPECTED_FINAL_BYTES),
            "finalWorkspaceBytes changed between Built and CacheHit")

stages = document["progressStages"]
require(isinstance(stages, list) and stages, "progressStages must be a non-empty array")
require(all(isinstance(stage, str) and stage.strip() for stage in stages),
        "progressStages must contain non-empty strings")
require(len(stages) == len(set(stages)), "progressStages must be de-duplicated")
require(stages[0] == "AcquiringLock" and stages[-1] == "Completed",
        "progressStages must span lock acquisition through completion")
if EXPECTED_STATUS == "Built":
    required = [
        "VerifyingCertificate", "VerifyingSources", "ScanningContent", "ExtractingContent",
        "ExtractingAssemblies", "WritingManifests", "ValidatingOutputs", "Committing",
        "RevalidatingInstallation", "Activating", "Completed",
    ]
    require(all(stage in stages for stage in required), "Built progressStages omit a required real stage")
    require("ValidatingCache" not in stages, "Built progressStages must not claim a cache hit")
    require_order(stages, required)
else:
    required = ["VerifyingCertificate", "ValidatingCache", "RevalidatingInstallation", "Activating", "Completed"]
    require(all(stage in stages for stage in required), "CacheHit progressStages omit a required real stage")
    forbidden = {"VerifyingSources", "ScanningContent", "ExtractingContent", "ExtractingAssemblies",
                 "WritingManifests", "ValidatingOutputs", "Committing"}
    require(not forbidden.intersection(stages), "CacheHit progressStages falsely claim extraction or commit")
    require_order(stages, required)

diagnostics = document["diagnostics"]
require(isinstance(diagnostics, list), "diagnostics must be an array")
for index, diagnostic in enumerate(diagnostics):
    location = f"$.diagnostics[{index}]"
    require(isinstance(diagnostic, dict), f"{location} must be an object")
    require(set(diagnostic) == {"timestamp", "stage", "severity", "code", "message"},
            f"{location} fields are incomplete or unexpected")
    require_timestamp(diagnostic["timestamp"], f"{location}.timestamp")
    for field in ("stage", "severity", "code", "message"):
        require(isinstance(diagnostic[field], str) and diagnostic[field].strip(),
                f"{location}.{field} must be a non-empty string")

print(key)
print("\t".join(str(statistics[field]) for field in
                ("contentFileCount", "contentBytes", "assemblyFileCount", "assemblyBytes")))
print(metrics["finalWorkspaceBytes"])
print("\t".join(str(metrics[field]) for field in
                ("durationMilliseconds", "peakTemporaryBytes", "finalWorkspaceBytes")))
PY
}

# Interruption recovery acceptance: wait until app-private staging contains a real payload,
# stop the process before activation, then require the next launch to rebuild and clean it.
printf 'Running process-interruption recovery acceptance...\n'
"${adb_device[@]}" logcat -c
"${adb_device[@]}" shell am force-stop "$package"
"${adb_device[@]}" shell am start -W -n "$component" >/dev/null
interrupted_pid="$(wait_for_pid)" || { echo "Could not resolve the JunimoGate App PID for interruption." >&2; exit 6; }
if ! wait_for_staging_payload; then
  echo "No app-private staging payload appeared within $interrupt_timeout_seconds seconds." >&2
  exit 6
fi
# Kill from the app UID instead of using am force-stop: this bypasses Activity cancellation
# and intentionally leaves the in-flight staging directory as sudden-death recovery evidence.
"${adb_device[@]}" shell run-as "$package" kill -9 "$interrupted_pid" >/dev/null 2>&1 || {
  echo "Could not kill the JunimoGate App process for interruption recovery." >&2
  exit 6
}
for _ in $(seq 1 40); do
  [[ -z "$("${adb_device[@]}" shell pidof "$package" 2>/dev/null | tr -d '\r\n' || true)" ]] && break
  sleep 0.05
done
if [[ -n "$("${adb_device[@]}" shell pidof "$package" 2>/dev/null | tr -d '\r\n' || true)" ]]; then
  echo "The interrupted JunimoGate App process is still running." >&2
  exit 6
fi
state_presence="$("${adb_device[@]}" shell "run-as $package sh -c 'if [ -f files/runtime/workspace-state.json ]; then echo exists; else echo missing; fi'" | tr -d '\r\n')"
if [[ "$state_presence" != "missing" ]]; then
  echo "Interrupted preparation unexpectedly activated workspace state (state: ${state_presence:-unknown})." >&2
  exit 6
fi
stale_staging_names="$("${adb_device[@]}" shell run-as "$package" ls -1 files/runtime/staging 2>/dev/null | tr -d '\r' || true)"
[[ -n "$stale_staging_names" ]] || { echo "Interrupted preparation left no staging directory to recover." >&2; exit 6; }
while IFS= read -r staging_name; do
  [[ "$staging_name" =~ ^[0-9a-f]{64}-[0-9a-f]{32}$ ]] || {
    echo "Interrupted preparation produced an unexpected staging directory name." >&2
    exit 6
  }
done <<<"$stale_staging_names"
"${adb_device[@]}" shell run-as "$package" rm -f files/reports/game-workspace-latest.json

"${adb_device[@]}" logcat -c
first_started=$SECONDS
"${adb_device[@]}" shell am start -W -n "$component" >/dev/null
first_pid="$(wait_for_pid)" || { echo "Could not resolve the first JunimoGate App PID." >&2; exit 6; }
printf 'Waiting up to %s seconds for the recovered first M4 report...\n' "$timeout_seconds"
if ! wait_for_report "$first_report"; then
  echo "JunimoGate App did not produce a valid first M4 report within $timeout_seconds seconds." >&2
  exit 6
fi
first_elapsed=$((SECONDS - first_started))
"${adb_device[@]}" logcat -d --pid="$first_pid" >>"$logcat_file"
if ! first_validation="$(validate_report "$first_report" Built)"; then
  exit 6
fi
mapfile -t first_values <<<"$first_validation"
[[ ${#first_values[@]} -eq 4 ]] || { echo "First report validator returned incomplete statistics or metrics." >&2; exit 6; }
first_key="${first_values[0]}"
IFS=$'\t' read -r content_count content_bytes assembly_count assembly_bytes <<<"${first_values[1]}"
first_final_bytes="${first_values[2]}"
IFS=$'\t' read -r first_prepare_ms first_peak_temporary_bytes first_metrics_final_bytes <<<"${first_values[3]}"
[[ "$first_metrics_final_bytes" == "$first_final_bytes" ]] || { echo "First report metrics disagree on final workspace bytes." >&2; exit 6; }
[[ "$first_key" =~ ^[0-9a-f]{64}$ ]] || { echo "Validated workspace key unexpectedly changed shape." >&2; exit 6; }

remaining_staging="$("${adb_device[@]}" shell run-as "$package" ls -1 files/runtime/staging 2>/dev/null | tr -d '\r' || true)"
if [[ -n "$remaining_staging" ]]; then
  while IFS= read -r staging_name; do
    if grep -Fxq "$staging_name" <<<"$stale_staging_names"; then
      echo "Recovered build did not clean interrupted staging directory: $staging_name" >&2
      exit 6
    fi
  done <<<"$remaining_staging"
fi

read_private_json files/runtime/workspace-state.json "$first_state"
read_private_json "files/runtime/workspaces/$first_key/source-manifest.json" "$source_manifest"
read_private_json "files/runtime/workspaces/$first_key/extraction-manifest.json" "$extraction_manifest"
read_private_json "files/runtime/workspaces/$first_key/rewrite-manifest.json" "$rewrite_manifest"

workspace_relative="files/runtime/workspaces/$first_key"
# A remote shell is needed only for cd/find/sha256sum. The sole variable inserted into
# this command is first_key, which is validated above as exactly 64 lowercase hex bytes.
hash_command="cd $workspace_relative && echo __JUNIMOGATE_FILES__ && find . -type f | sort && echo __JUNIMOGATE_HASHES__ && find Content assemblies -type f -exec sha256sum {} \\;"
"${adb_device[@]}" exec-out "run-as $package sh -c '$hash_command'" >"$hash_listing"

python3 - "$first_report" "$first_state" "$source_manifest" "$extraction_manifest" "$rewrite_manifest" "$hash_listing" <<'PY'
import json
import os
import re
import sys
from pathlib import PurePosixPath

REPORT_PATH, STATE_PATH, SOURCE_PATH, EXTRACTION_PATH, REWRITE_PATH, HASH_PATH = sys.argv[1:]
PLAY_PACKAGE = "com.chucklefish.stardewvalley"
TESTED_PLAY_CERTIFICATE = "c7b27f1faf2f350e3c117875bde2353cea837ebe1b3c2ce23513bb191d95852d"
HEX64 = re.compile(r"^[0-9a-f]{64}$")


def fail(message):
    raise SystemExit(f"Invalid active workspace evidence: {message}")


def require(condition, message):
    if not condition:
        fail(message)


def load(path):
    with open(path, "r", encoding="utf-8") as stream:
        value = json.load(stream)
    require(isinstance(value, dict), f"{path} must contain a JSON object")
    return value


def safe_relative(value, location, minimum_segments=1):
    require(isinstance(value, str) and value, f"{location} must be a non-empty string")
    require(not value.startswith("/") and "\\" not in value and "\x00" not in value,
            f"{location} must be a safe relative path")
    parts = value.split("/")
    require(len(parts) >= minimum_segments and all(part not in ("", ".", "..") for part in parts),
            f"{location} has unsafe path segments")
    require(not any(ord(character) < 32 for character in value), f"{location} contains control characters")
    return value


def reject_absolute_strings(value, location="$"):
    if isinstance(value, dict):
        for key, child in value.items():
            normalized = re.sub(r"[^a-z0-9]", "", str(key).casefold())
            require(normalized not in {"sourcepath", "workspacepath", "apkpath", "deviceid", "androidid", "serial", "imei"},
                    f"{location} contains forbidden key {key!r}")
            reject_absolute_strings(child, f"{location}.{key}")
    elif isinstance(value, list):
        for index, child in enumerate(value):
            reject_absolute_strings(child, f"{location}[{index}]")
    elif isinstance(value, str):
        require(not value.startswith("/") and re.match(r"^[A-Za-z]:[\\/]", value) is None,
                f"{location} contains an absolute path")
        require("/data/" not in value and "/storage/" not in value and "/sdcard/" not in value,
                f"{location} contains a device path")


report = load(REPORT_PATH)
state = load(STATE_PATH)
source = load(SOURCE_PATH)
extraction = load(EXTRACTION_PATH)
rewrite = load(REWRITE_PATH)
for document in (state, source, extraction, rewrite):
    reject_absolute_strings(document)
key = report["workspaceKey"]
statistics = report["statistics"]
metrics = report["metrics"]

require(set(state) == {"format", "schema", "activeKey", "previousKey"}, "workspace state fields are unexpected")
require(state["format"] == "junimogate-workspace-state" and state["schema"] == "v1",
        "workspace state format or schema is incorrect")
require(state["activeKey"] == key, "workspace state activeKey does not match the report")
require(state["previousKey"] is None, "workspace state previousKey must be null on the first activation")

require(set(source) == {
    "format", "schema", "cacheKey", "packageName", "versionName", "longVersionCode",
    "abi", "signers", "sources",
}, "source manifest fields are incomplete or unexpected")
require(source["format"] == "junimogate-source-manifest", "source manifest format is incorrect")
require(source["schema"] == "junimogate-workspace-manifest:v1", "source manifest schema is incorrect")
require(source["cacheKey"] == key and source["packageName"] == PLAY_PACKAGE,
        "source manifest identity does not match the report")
require(isinstance(source["versionName"], str) and source["versionName"].strip(), "source versionName is missing")
require(isinstance(source["longVersionCode"], int) and not isinstance(source["longVersionCode"], bool)
        and source["longVersionCode"] >= 0, "source longVersionCode is invalid")
require(source["abi"] == "arm64-v8a", "source ABI must be arm64-v8a")
signers = source["signers"]
require(isinstance(signers, dict) and set(signers) == {"current", "history"}, "source signers are invalid")
current = signers["current"]
history = signers["history"]
require(isinstance(current, list) and current and isinstance(history, list), "source signer arrays are invalid")
for location, values in (("current", current), ("history", history)):
    require(all(isinstance(value, str) and HEX64.fullmatch(value) for value in values),
            f"source signer {location} contains an invalid digest")
    require(len(values) == len(set(values)), f"source signer {location} contains duplicates")
require(current == sorted(current), "source current signers must be sorted")
if len(current) == 1 and history:
    require(history[-1] == current[0], "source signer history must end with the current signer")
require(TESTED_PLAY_CERTIFICATE in current or TESTED_PLAY_CERTIFICATE in history,
        "source signer identity does not include the tested Play certificate")

sources = source["sources"]
require(isinstance(sources, list) and sources, "source APK identities must be non-empty")
source_labels = set()
for index, item in enumerate(sources):
    location = f"$.sources[{index}]"
    require(isinstance(item, dict) and set(item) == {"label", "splitName", "sha256", "size"},
            f"{location} fields are invalid")
    label = item["label"]
    require(isinstance(label, str) and label and "/" not in label and "\\" not in label,
            f"{location}.label is invalid")
    require(label not in source_labels, f"{location}.label is duplicated")
    source_labels.add(label)
    require(item["splitName"] is None or (isinstance(item["splitName"], str) and item["splitName"].strip()),
            f"{location}.splitName is invalid")
    require(isinstance(item["sha256"], str) and HEX64.fullmatch(item["sha256"]),
            f"{location}.sha256 is invalid")
    require(isinstance(item["size"], int) and not isinstance(item["size"], bool) and item["size"] > 0,
            f"{location}.size must be positive")
require("base" in source_labels, "source APK identities must include the base source")

require(set(extraction) == {
    "format", "schema", "cacheKey", "extractorSchema", "rewriterRecipe", "smapiBuildId",
    "files", "statistics",
}, "extraction manifest fields are incomplete or unexpected")
require(extraction["format"] == "junimogate-extraction-manifest", "extraction manifest format is incorrect")
require(extraction["schema"] == "junimogate-workspace-manifest:v1", "extraction manifest schema is incorrect")
require(extraction["cacheKey"] == key, "extraction manifest cacheKey does not match the report")
require(extraction["extractorSchema"] == "junimogate-extraction:v1", "extractor schema is incorrect")
require(extraction["rewriterRecipe"] == "unrewritten:v1", "rewriter recipe is incorrect")
require(extraction["smapiBuildId"] == "none", "SMAPI build identity must be none")
require(extraction["statistics"] == statistics, "extraction statistics do not match the report")

require(set(rewrite) == {"format", "schema", "cacheKey", "recipe", "status"},
        "rewrite manifest fields are incomplete or unexpected")
require(rewrite["format"] == "junimogate-rewrite-manifest", "rewrite manifest format is incorrect")
require(rewrite["schema"] == "junimogate-workspace-manifest:v1", "rewrite manifest schema is incorrect")
require(rewrite["cacheKey"] == key, "rewrite manifest cacheKey does not match the report")
require(rewrite["recipe"] == "unrewritten:v1", "rewrite manifest recipe is incorrect")
require(rewrite["status"] == "not-applied", "rewrite manifest must state that rewriting was not applied")

files = extraction["files"]
require(isinstance(files, list) and files, "extraction files must be non-empty")
manifest_files = {}
computed = {"contentFileCount": 0, "contentBytes": 0, "assemblyFileCount": 0, "assemblyBytes": 0}
required_assemblies = set()
for index, item in enumerate(files):
    location = f"$.files[{index}]"
    require(isinstance(item, dict) and set(item) == {
        "kind", "relativePath", "size", "sha256", "sourceLabel", "sourceEntry",
    }, f"{location} fields are invalid")
    relative = safe_relative(item["relativePath"], f"{location}.relativePath", minimum_segments=2)
    require(relative not in {"source-manifest.json", "extraction-manifest.json", "rewrite-manifest.json"},
            f"{location} attempts to treat a manifest as payload")
    require(relative not in manifest_files, f"{location}.relativePath is duplicated")
    kind = item["kind"]
    require(kind in {"content", "assembly"}, f"{location}.kind is invalid")
    expected_prefix = "Content/" if kind == "content" else "assemblies/"
    require(relative.startswith(expected_prefix), f"{location}.relativePath has the wrong root")
    require(isinstance(item["size"], int) and not isinstance(item["size"], bool) and item["size"] >= 0,
            f"{location}.size is invalid")
    require(isinstance(item["sha256"], str) and HEX64.fullmatch(item["sha256"]),
            f"{location}.sha256 is invalid")
    require(item["sourceLabel"] in source_labels, f"{location}.sourceLabel is unknown")
    safe_relative(item["sourceEntry"], f"{location}.sourceEntry", minimum_segments=2)
    manifest_files[relative] = item
    if kind == "content":
        computed["contentFileCount"] += 1
        computed["contentBytes"] += item["size"]
    else:
        computed["assemblyFileCount"] += 1
        computed["assemblyBytes"] += item["size"]
        required_assemblies.add(PurePosixPath(relative).name.casefold())
require(computed == statistics, "computed extraction statistics do not match the manifest")
require({"stardewvalley.dll", "monogame.framework.dll"} <= required_assemblies,
        "required game assemblies are missing")
expected_final_bytes = sum(item["size"] for item in manifest_files.values()) + sum(
    os.path.getsize(path) for path in (SOURCE_PATH, EXTRACTION_PATH, REWRITE_PATH))
require(metrics["finalWorkspaceBytes"] == expected_final_bytes,
        "report finalWorkspaceBytes does not equal payload plus the three manifest files")
require(metrics["peakTemporaryBytes"] == expected_final_bytes,
        "Built peakTemporaryBytes does not equal the complete pre-commit staging size")

with open(HASH_PATH, "r", encoding="utf-8") as stream:
    lines = [line.rstrip("\r\n") for line in stream]
try:
    files_marker = lines.index("__JUNIMOGATE_FILES__")
    hashes_marker = lines.index("__JUNIMOGATE_HASHES__")
except ValueError:
    fail("device-side file/hash listing markers are missing")
require(files_marker == 0 and hashes_marker > files_marker, "device-side listing markers are malformed")
actual_all_files = {line[2:] if line.startswith("./") else line for line in lines[files_marker + 1:hashes_marker]}
require(actual_all_files == set(manifest_files) | {
    "source-manifest.json", "extraction-manifest.json", "rewrite-manifest.json",
}, "actual active workspace file set differs from payload plus exactly three manifests")

actual_hashes = {}
for line in lines[hashes_marker + 1:]:
    match = re.fullmatch(r"([0-9a-f]{64})\s+(.+)", line)
    require(match is not None, "device-side sha256sum output is malformed")
    digest, relative = match.groups()
    relative = relative[2:] if relative.startswith("./") else relative
    require(relative not in actual_hashes, "device-side hash listing contains a duplicate file")
    actual_hashes[relative] = digest
require(set(actual_hashes) == set(manifest_files), "hashed payload file set differs from the extraction manifest")
for relative, item in manifest_files.items():
    require(actual_hashes[relative] == item["sha256"], f"payload hash mismatch for {relative!r}")
PY

printf 'Recovered first run passed in %ss end-to-end / %sms prepare: key=%s, Content=%s files/%s bytes, assemblies=%s files/%s bytes, temporary=%s bytes, final=%s bytes.\n' \
  "$first_elapsed" "$first_prepare_ms" "$first_key" "$content_count" "$content_bytes" "$assembly_count" "$assembly_bytes" "$first_peak_temporary_bytes" "$first_final_bytes"

"${adb_device[@]}" shell am force-stop "$package"
"${adb_device[@]}" shell run-as "$package" rm -f files/reports/game-workspace-latest.json
"${adb_device[@]}" logcat -c
second_started=$SECONDS
"${adb_device[@]}" shell am start -W -n "$component" >/dev/null
second_pid="$(wait_for_pid)" || { echo "Could not resolve the second JunimoGate App PID." >&2; exit 7; }
printf 'Waiting up to %s seconds for the second M4 report...\n' "$timeout_seconds"
if ! wait_for_report "$second_report"; then
  echo "JunimoGate App did not produce a valid second M4 report within $timeout_seconds seconds." >&2
  exit 7
fi
second_elapsed=$((SECONDS - second_started))
"${adb_device[@]}" logcat -d --pid="$second_pid" >>"$logcat_file"
if ! second_validation="$(validate_report "$second_report" CacheHit "$first_key" "$first_final_bytes")"; then
  exit 7
fi
mapfile -t second_values <<<"$second_validation"
[[ ${#second_values[@]} -eq 4 ]] || { echo "Second report validator returned incomplete statistics or metrics." >&2; exit 7; }
second_key="${second_values[0]}"
IFS=$'\t' read -r second_content_count second_content_bytes second_assembly_count second_assembly_bytes <<<"${second_values[1]}"
second_final_bytes="${second_values[2]}"
IFS=$'\t' read -r second_prepare_ms second_peak_temporary_bytes second_metrics_final_bytes <<<"${second_values[3]}"
[[ "$second_metrics_final_bytes" == "$second_final_bytes" ]] || { echo "Second report metrics disagree on final workspace bytes." >&2; exit 7; }
if [[ "$content_count:$content_bytes:$assembly_count:$assembly_bytes" != \
      "$second_content_count:$second_content_bytes:$second_assembly_count:$second_assembly_bytes" ]]; then
  echo "Workspace statistics changed between the Built and CacheHit reports." >&2
  exit 8
fi

read_private_json files/runtime/workspace-state.json "$second_state"
python3 - "$second_state" "$second_key" <<'PY'
import json
import re
import sys

with open(sys.argv[1], "r", encoding="utf-8") as stream:
    state = json.load(stream)
key = sys.argv[2]
if not isinstance(state, dict) or set(state) != {"format", "schema", "activeKey", "previousKey"}:
    raise SystemExit("Invalid second workspace state: unexpected fields")
if state["format"] != "junimogate-workspace-state" or state["schema"] != "v1":
    raise SystemExit("Invalid second workspace state: format or schema mismatch")
if not re.fullmatch(r"[0-9a-f]{64}", key) or state["activeKey"] != key:
    raise SystemExit("Invalid second workspace state: activeKey mismatch")
if state["previousKey"] is not None:
    raise SystemExit("Invalid second workspace state: previousKey must remain null")
PY

if grep -E 'FATAL EXCEPTION|AndroidRuntime[^:]*:.*FATAL' "$logcat_file" >/dev/null; then
  echo "JunimoGate App PID-filtered logcat contains a FATAL EXCEPTION or AndroidRuntime crash." >&2
  grep -E 'FATAL EXCEPTION|AndroidRuntime[^:]*:.*FATAL' "$logcat_file" >&2 || true
  exit 9
fi

artifact_dir="$root/artifacts/android"
mkdir -p "$artifact_dir"
artifact="$artifact_dir/game-workspace-debug.json"
cp "$second_report" "$artifact"

printf 'Second run passed in %ss end-to-end / %sms prepare: status=CacheHit, key=%s, Content=%s files/%s bytes, assemblies=%s files/%s bytes, temporary=%s bytes, final=%s bytes.\n' \
  "$second_elapsed" "$second_prepare_ms" "$second_key" "$second_content_count" "$second_content_bytes" "$second_assembly_count" "$second_assembly_bytes" "$second_peak_temporary_bytes" "$second_final_bytes"
printf 'Workspace manifests, payload hashes/listing, PID-filtered logcat, and interruption evidence were kept only in temporary files and deleted on exit.\n'
printf 'Redacted M4 report: %s\n' "$artifact"
