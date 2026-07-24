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
package="org.junimogate.app"
component="$package/$package.MainActivity"

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
if [[ ",$abis," != *,arm64-v8a,* ]]; then
  echo "JunimoGate.App requires an ARM64 device; selected device reports ABI list: $abis" >&2
  exit 3
fi
printf 'Device: model=%s API=%s ABI=%s\n' "${model:-unknown}" "${api:-unknown}" "$abis"

"$root/build/build-android.sh" "$configuration" app

output_dir="$root/src/JunimoGate.App/bin/$configuration/net9.0-android35.0/android-arm64"
apk="$output_dir/org.junimogate.app-Signed.apk"
[[ -f "$apk" ]] || { echo "No current signed ARM64 JunimoGate App APK found at $apk." >&2; exit 4; }

printf 'Installing signed JunimoGate App (%s)...\n' "$configuration"
"${adb_device[@]}" install --no-incremental -r -t "$apk" >/dev/null
"${adb_device[@]}" shell am force-stop "$package"
"${adb_device[@]}" shell pm clear "$package" >/dev/null
if ! "${adb_device[@]}" shell run-as "$package" true >/dev/null 2>&1; then
  echo "The installed App does not permit run-as access to its app-private report. Use a debuggable configuration (normally Debug)." >&2
  exit 5
fi
"${adb_device[@]}" shell am force-stop "$package"
"${adb_device[@]}" shell am start -W -n "$component" >/dev/null

artifact_dir="$root/artifacts/android"
mkdir -p "$artifact_dir"
report="$artifact_dir/game-discovery-${configuration,,}.json"
temporary="$(mktemp)"
trap 'rm -f "$temporary"' EXIT

printf 'Waiting for app-private files/reports/game-discovery-latest.json...\n'
completed=false
for _ in $(seq 1 120); do
  if "${adb_device[@]}" exec-out run-as "$package" cat files/reports/game-discovery-latest.json >"$temporary" 2>/dev/null \
    && python3 -m json.tool "$temporary" >/dev/null 2>&1; then
    completed=true
    break
  fi
  sleep 1
done

if [[ "$completed" != true ]]; then
  echo "JunimoGate App did not produce a valid app-private game discovery report within 120 seconds." >&2
  exit 6
fi

branch="$(python3 - "$temporary" <<'PY'
import json
import re
import sys

EXPECTED_PACKAGES = [
    "com.chucklefish.stardewvalley",
    "com.chucklefish.stardewvalleysamsung",
]
EXPECTED_ROLES = {
    "game-content",
    "legacy-assembly-blob",
    "modern-assembly-blob",
}
FORBIDDEN_KEYS = {
    "sourcepath",
    "path",
    "deviceid",
    "deviceidentifier",
    "androidid",
    "serial",
    "serialnumber",
    "imei",
}
HEX64 = re.compile(r"^[0-9a-f]{64}$")
ABI = re.compile(r"^[a-z0-9][a-z0-9._-]*$")
TESTED_PLAY_CERTIFICATE = "c7b27f1faf2f350e3c117875bde2353cea837ebe1b3c2ce23513bb191d95852d"


def fail(message):
    raise SystemExit(f"Invalid game discovery report: {message}")


def require(condition, message):
    if not condition:
        fail(message)


def normalized_key(value):
    return re.sub(r"[^a-z0-9]", "", value.casefold())


def reject_sensitive(value, location="$"):
    if isinstance(value, dict):
        for key, child in value.items():
            require(isinstance(key, str), f"{location} contains a non-string object key")
            require(normalized_key(key) not in FORBIDDEN_KEYS, f"{location} contains forbidden key {key!r}")
            reject_sensitive(child, f"{location}.{key}")
    elif isinstance(value, list):
        for index, child in enumerate(value):
            reject_sensitive(child, f"{location}[{index}]")
    elif isinstance(value, str):
        require(not value.startswith("/"), f"{location} contains an absolute path")
        require(re.match(r"^[A-Za-z]:[\\/]", value) is None, f"{location} contains an absolute path")


def require_string(value, location, allow_empty=False):
    require(isinstance(value, str), f"{location} must be a string")
    if not allow_empty:
        require(bool(value.strip()), f"{location} must not be empty")
    return value


def validate_string_list(value, location, allowed=None, abi=False):
    require(isinstance(value, list), f"{location} must be an array")
    for index, item in enumerate(value):
        item = require_string(item, f"{location}[{index}]")
        if allowed is not None:
            require(item in allowed, f"{location}[{index}] has unsupported value {item!r}")
        if abi:
            require(ABI.fullmatch(item) is not None and item == item.lower(), f"{location}[{index}] is not a canonical ABI")
    require(value == sorted(set(value)), f"{location} must be unique and sorted")


def validate_diagnostic(value, location, expected_package=None):
    require(isinstance(value, dict), f"{location} must be an object")
    package_name = require_string(value.get("packageName"), f"{location}.packageName")
    require(package_name in EXPECTED_PACKAGES, f"{location}.packageName is unexpected")
    if expected_package is not None:
        require(package_name == expected_package, f"{location}.packageName does not match its package report")
    require_string(value.get("timestampUtc"), f"{location}.timestampUtc")
    require_string(value.get("stage"), f"{location}.stage")
    require_string(value.get("severity"), f"{location}.severity")
    require_string(value.get("code"), f"{location}.code")
    require_string(value.get("message"), f"{location}.message")


def validate_candidate(value, location, expected_package=None):
    require(isinstance(value, dict), f"{location} must be an object")
    package_name = require_string(value.get("packageName"), f"{location}.packageName")
    require(package_name in EXPECTED_PACKAGES, f"{location}.packageName is unexpected")
    if expected_package is not None:
        require(package_name == expected_package, f"{location}.packageName does not match its package report")
    require_string(value.get("versionName"), f"{location}.versionName")
    version_code = value.get("longVersionCode")
    require(isinstance(version_code, int) and not isinstance(version_code, bool) and version_code >= 0,
            f"{location}.longVersionCode must be a non-negative integer")
    require(value.get("selectedAbi") == "arm64-v8a", f"{location}.selectedAbi must be arm64-v8a")

    signing = value.get("signing")
    require(isinstance(signing, dict), f"{location}.signing must be an object")
    current = signing.get("currentSignerDigests")
    history = signing.get("rotationHistory")
    require(isinstance(current, list) and len(current) >= 1,
            f"{location}.signing.currentSignerDigests must be a non-empty array")
    require(isinstance(history, list), f"{location}.signing.rotationHistory must be an array")
    for digest_location, digests in (
        (f"{location}.signing.currentSignerDigests", current),
        (f"{location}.signing.rotationHistory", history),
    ):
        for index, digest in enumerate(digests):
            require(isinstance(digest, str) and HEX64.fullmatch(digest) is not None,
                    f"{digest_location}[{index}] must be 64 lowercase hexadecimal characters")
        require(len(digests) == len(set(digests)), f"{digest_location} must not contain duplicates")
    require(current == sorted(current), f"{location}.signing.currentSignerDigests must be sorted")
    if len(current) > 1:
        require(not history, f"{location}.signing.rotationHistory must be empty for multiple current signers")
    elif history:
        require(history[-1] == current[0], f"{location}.signing.rotationHistory must end with the current signer")

    status = require_string(value.get("gameCertificateStatus"), f"{location}.gameCertificateStatus")
    allows_execution = value.get("allowsCodeExecution")
    require(isinstance(allows_execution, bool), f"{location}.allowsCodeExecution must be a boolean")
    matched_certificate = value.get("matchedKnownCertificateSha256")
    if package_name == "com.chucklefish.stardewvalley":
        if current == [TESTED_PLAY_CERTIFICATE]:
            expected_status = "KnownTested"
        elif (len(current) == 1 and len(history) > 1 and
              history[-1] == current[0] and TESTED_PLAY_CERTIFICATE in history[:-1]):
            expected_status = "KnownTestedAfterRotation"
        else:
            expected_status = "Unrecognized"
        expected_allows = expected_status in {"KnownTested", "KnownTestedAfterRotation"}
        expected_match = TESTED_PLAY_CERTIFICATE if expected_allows else None
    else:
        expected_status = "NotConfigured"
        expected_allows = False
        expected_match = None
    require(status == expected_status,
            f"{location}.gameCertificateStatus does not match the independently evaluated signer identity")
    require(allows_execution is expected_allows,
            f"{location}.allowsCodeExecution does not match the certificate status")
    require(matched_certificate == expected_match,
            f"{location}.matchedKnownCertificateSha256 does not match the certificate status")

    sources = value.get("apkSources")
    require(isinstance(sources, list) and len(sources) >= 1, f"{location}.apkSources must be a non-empty array")
    labels = []
    all_roles = set()
    for index, source in enumerate(sources):
        source_location = f"{location}.apkSources[{index}]"
        require(isinstance(source, dict), f"{source_location} must be an object")
        labels.append(require_string(source.get("label"), f"{source_location}.label"))
        digest = source.get("sha256")
        require(isinstance(digest, str) and HEX64.fullmatch(digest) is not None,
                f"{source_location}.sha256 must be 64 lowercase hexadecimal characters")
        size = source.get("sizeBytes")
        require(isinstance(size, int) and not isinstance(size, bool) and size > 0,
                f"{source_location}.sizeBytes must be a positive integer")
        roles = source.get("roles")
        validate_string_list(roles, f"{source_location}.roles", allowed=EXPECTED_ROLES)
        validate_string_list(source.get("nativeAbis"), f"{source_location}.nativeAbis", abi=True)
        validate_string_list(source.get("assemblyStoreAbis"), f"{source_location}.assemblyStoreAbis", abi=True)
        all_roles.update(roles)
    require(len(labels) == len(set(labels)), f"{location}.apkSources labels must be unique")
    require("game-content" in all_roles, f"{location}.apkSources must include the game-content role")
    require(
        bool({"legacy-assembly-blob", "modern-assembly-blob"} & all_roles),
        f"{location}.apkSources must include an assembly role",
    )
    return package_name


with open(sys.argv[1], "r", encoding="utf-8") as stream:
    document = json.load(stream)

require(isinstance(document, dict), "root must be an object")
reject_sensitive(document)
require(document.get("formatVersion") == 2, "formatVersion must equal 2")
require_string(document.get("generatedAtUtc"), "$.generatedAtUtc")
package_reports = document.get("packageReports")
require(isinstance(package_reports, list) and len(package_reports) == 2,
        "packageReports must contain exactly two entries")
require([value.get("packageName") if isinstance(value, dict) else None for value in package_reports] == EXPECTED_PACKAGES,
        "packageReports must contain the two exact supported packages in deterministic order")

successful_candidates = []
flattened_diagnostics = []
for index, package_report in enumerate(package_reports):
    location = f"$.packageReports[{index}]"
    package_name = EXPECTED_PACKAGES[index]
    require(isinstance(package_report, dict), f"{location} must be an object")
    require(package_report.get("packageName") == package_name, f"{location}.packageName is incorrect")
    is_success = package_report.get("isSuccess")
    require(isinstance(is_success, bool), f"{location}.isSuccess must be a boolean")
    candidate = package_report.get("candidate")
    diagnostics = package_report.get("diagnostics")
    require(isinstance(diagnostics, list), f"{location}.diagnostics must be an array")
    for diagnostic_index, diagnostic in enumerate(diagnostics):
        validate_diagnostic(diagnostic, f"{location}.diagnostics[{diagnostic_index}]", package_name)
    flattened_diagnostics.extend(diagnostics)
    if is_success:
        require(candidate is not None, f"{location}.candidate is required when isSuccess is true")
        validate_candidate(candidate, f"{location}.candidate", package_name)
        successful_candidates.append(candidate)
    else:
        require(candidate is None, f"{location}.candidate must be null when isSuccess is false")

candidates = document.get("candidates")
require(isinstance(candidates, list), "candidates must be an array")
for index, candidate in enumerate(candidates):
    validate_candidate(candidate, f"$.candidates[{index}]")
require(candidates == successful_candidates, "candidates must exactly mirror successful packageReports candidates")
require(len({candidate["packageName"] for candidate in candidates}) == len(candidates),
        "candidates must have unique package names")

diagnostics = document.get("diagnostics")
require(isinstance(diagnostics, list), "diagnostics must be an array")
for index, diagnostic in enumerate(diagnostics):
    validate_diagnostic(diagnostic, f"$.diagnostics[{index}]")
require(diagnostics == flattened_diagnostics, "diagnostics must exactly flatten packageReports diagnostics")

if not candidates:
    for index, package_report in enumerate(package_reports):
        codes = [diagnostic["code"] for diagnostic in package_report["diagnostics"]]
        require(codes == ["package_not_found_or_not_visible"],
                f"$.packageReports[{index}] must contain only package_not_found_or_not_visible in the missing-package branch")
    print("missing-package")
else:
    require(len(candidates) >= 1, "full-candidate branch must contain at least one candidate")
    print("full-candidate")
PY
)"

mv "$temporary" "$report"
trap - EXIT

case "$branch" in
  missing-package)
    printf 'Game discovery verification passed: missing-package branch only.\n'
    printf 'Both supported packages reported package_not_found_or_not_visible. This is partial device evidence and does not complete M3.\n'
    ;;
  full-candidate)
    candidate_count="$(python3 - "$report" <<'PY'
import json
import sys
with open(sys.argv[1], "r", encoding="utf-8") as stream:
    print(len(json.load(stream)["candidates"]))
PY
)"
    printf 'Game discovery verification passed: full-candidate branch with %s candidate(s).\n' "$candidate_count"
    printf 'The candidate report passed certificate identity, signer, hash, source role, ABI, structure, and redaction checks.\n'
    ;;
  *)
    echo "Unexpected verifier branch: $branch" >&2
    exit 7
    ;;
esac
printf 'Raw redacted App report: %s\n' "$report"
