#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=android-env.sh
source "$root/build/android-env.sh"

adb="$ANDROID_SDK_ROOT/platform-tools/adb"
package="org.junimogate.app"
component="$package/$package.MainActivity"
play_package="com.chucklefish.stardewvalley"
expected_game_version="${JUNIMOGATE_GATE0_GAME_VERSION:-1.6.15.3}"
expected_game_version_code="${JUNIMOGATE_GATE0_GAME_VERSION_CODE:-245}"
timeout_seconds="${JUNIMOGATE_GAMEHOST_PROBE_TIMEOUT_SECONDS:-1200}"

if [[ ! "$timeout_seconds" =~ ^[0-9]+$ ]] || ((timeout_seconds < 120)); then
  echo "JUNIMOGATE_GAMEHOST_PROBE_TIMEOUT_SECONDS must be an integer of at least 120." >&2
  exit 2
fi
if [[ ! "$expected_game_version_code" =~ ^[0-9]+$ ]]; then
  echo "JUNIMOGATE_GATE0_GAME_VERSION_CODE must be a non-negative integer." >&2
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
if ! "${adb_device[@]}" shell pm path "$play_package" 2>/dev/null | grep -q '^package:'; then
  echo "The tested Google Play game package is not installed or visible on the selected device." >&2
  exit 3
fi
printf 'Device: model=%s API=%s ABI=%s\n' "${model:-unknown}" "$api" "$abis"

"$root/build/build-android.sh" Debug app
output_dir="$root/src/JunimoGate.App/bin/Debug/net9.0-android35.0/android-arm64"
apk="$output_dir/org.junimogate.app-Signed.apk"
[[ -f "$apk" ]] || { echo "No current signed ARM64 Debug APK found at $apk." >&2; exit 4; }

python3 - "$apk" <<'PY'
import pathlib
import sys
import zipfile

path = pathlib.Path(sys.argv[1])
with zipfile.ZipFile(path) as archive:
    prohibited = []
    for name in archive.namelist():
        folded = name.casefold()
        basename = pathlib.PurePosixPath(name).name.casefold()
        if (
            "stardewvalley.dll" in folded
            or "monogame.framework.dll" in folded
            or "assets/content/" in folded
            or basename.startswith("libaot-")
        ):
            prohibited.append(name)
if prohibited:
    raise SystemExit("JunimoGate App APK contains prohibited game/AOT payload entries")
PY

printf 'Installing signed JunimoGate App (Debug)...\n'
"${adb_device[@]}" install --no-incremental -r -t "$apk" >/dev/null
"${adb_device[@]}" shell am force-stop "$package"
"${adb_device[@]}" shell pm clear "$package" >/dev/null
if ! "${adb_device[@]}" shell run-as "$package" true >/dev/null 2>&1; then
  echo "The installed Debug App does not permit run-as access to its app-private reports." >&2
  exit 5
fi

work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT
first_probe="$work_dir/first-probe.json"
first_workspace="$work_dir/first-workspace.json"
first_discovery="$work_dir/first-discovery.json"
second_probe="$work_dir/second-probe.json"
second_workspace="$work_dir/second-workspace.json"
second_discovery="$work_dir/second-discovery.json"
logcat_file="$work_dir/logcat.txt"

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

wait_for_probe_report() {
  local destination="$1"
  local started=$SECONDS
  local partial="$destination.partial"
  while ((SECONDS - started < timeout_seconds)); do
    if "${adb_device[@]}" exec-out run-as "$package" cat files/reports/gamehost-probe-latest.json >"$partial" 2>/dev/null \
      && python3 -m json.tool "$partial" >/dev/null 2>&1; then
      mv "$partial" "$destination"
      return 0
    fi
    sleep 1
  done
  rm -f "$partial"
  return 1
}

read_private_json() {
  local private_path="$1"
  local destination="$2"
  "${adb_device[@]}" exec-out run-as "$package" cat "$private_path" >"$destination"
  python3 -m json.tool "$destination" >/dev/null
}

validate_probe_report() {
  local probe_report="$1"
  local workspace_report="$2"
  local discovery_report="$3"
  local expected_workspace_status="$4"
  local expected_support_key="${5:-}"
  python3 - "$probe_report" "$workspace_report" "$discovery_report" \
    "$expected_workspace_status" "$expected_support_key" "$serial" \
    "$expected_game_version" "$expected_game_version_code" <<'PY'
import datetime
import hashlib
import json
import pathlib
import re
import sys
import unicodedata
import uuid

(
    PROBE_PATH,
    WORKSPACE_PATH,
    DISCOVERY_PATH,
    EXPECTED_WORKSPACE_STATUS,
    EXPECTED_SUPPORT_KEY,
    DEVICE_SERIAL,
    EXPECTED_GAME_VERSION,
    EXPECTED_GAME_VERSION_CODE,
) = sys.argv[1:]

PLAY_PACKAGE = "com.chucklefish.stardewvalley"
SELECTED_ABI = "arm64-v8a"
PROBE_FORMAT = "junimogate-gamehost-probe-report"
PROBE_SCHEMA = "junimogate.gamehost-probe/v1"
SUPPORT_SCHEMA = "junimogate.gamehost-support-key/v1"
HEX64 = re.compile(r"^[0-9a-f]{64}$")
LIBRARY_NAME = re.compile(r"^[A-Za-z0-9._+\-]+\.so$")
WINDOWS_ABSOLUTE = re.compile(r"^[A-Za-z]:[\\/]")
FORBIDDEN_KEYS = {
    "sourcepath", "workspacepath", "apkpath", "deviceid", "deviceidentifier",
    "androidid", "serial", "serialnumber", "imei", "rawbytes", "ilbody",
}
SEVERITIES = {"Information", "Warning", "Error"}
FIELD_OPERATIONS = {"Read", "Write", "Address", "Other"}


def fail(message):
    raise SystemExit(f"Invalid Gate 0 report: {message}")


def require(condition, message):
    if not condition:
        fail(message)


def load(path):
    with open(path, "r", encoding="utf-8") as stream:
        value = json.load(stream)
    require(isinstance(value, dict), f"{path} must contain a JSON object")
    return value


def require_fields(value, fields, location):
    require(isinstance(value, dict), f"{location} must be an object")
    require(set(value) == set(fields), f"{location} fields are incomplete or unexpected")


def require_string(value, location, maximum=4096):
    require(isinstance(value, str) and value.strip(), f"{location} must be a non-empty string")
    require(len(value.encode("utf-8")) <= maximum, f"{location} exceeds its bounded UTF-8 length")
    return value


def require_int(value, location, minimum=0, maximum=None):
    require(isinstance(value, int) and not isinstance(value, bool), f"{location} must be an integer")
    require(value >= minimum, f"{location} is below its minimum")
    if maximum is not None:
        require(value <= maximum, f"{location} exceeds its maximum")
    return value


def require_timestamp(value, location):
    require_string(value, location, 128)
    try:
        parsed = datetime.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        fail(f"{location} is not an ISO-8601 timestamp")
    require(parsed.tzinfo is not None, f"{location} must include an offset")


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
        folded = value.casefold()
        require("/data/" not in folded and "/storage/" not in folded and "/sdcard/" not in folded,
                f"{location} contains a device path")
        require(DEVICE_SERIAL not in value, f"{location} contains the adb serial")
        require(len(value.encode("utf-8")) <= 4096, f"{location} contains an oversized string")


def validate_string_array(value, location, allow_empty=True, sorted_unique=False):
    require(isinstance(value, list), f"{location} must be an array")
    if not allow_empty:
        require(value, f"{location} must not be empty")
    for index, item in enumerate(value):
        require_string(item, f"{location}[{index}]")
    if sorted_unique:
        require(value == sorted(set(value), key=ordinal_key), f"{location} must be ordinal-sorted and unique")
    return value


def ordinal_key(value):
    return value.encode("utf-16-be", errors="surrogatepass")


def bool_text(value):
    return "True" if value else "False"


def encode_field(name, value):
    name_bytes = name.encode("utf-8")
    value_bytes = value.encode("utf-8")
    return f"{len(name_bytes)}:{name}={len(value_bytes)}:{value}\n"


class CanonicalHash:
    def __init__(self):
        self.digest = hashlib.sha256()

    def add(self, name, value):
        self.digest.update(encode_field(name, value).encode("utf-8"))

    def add_array(self, name, values):
        values = list(values)
        self.add(f"{name}.count", str(len(values)))
        for index, value in enumerate(values):
            self.add(f"{name}[{index}]", value)

    def hexdigest(self):
        return self.digest.hexdigest()


def encode_fields(fields):
    return "".join(encode_field(name, str(value)) for name, value in fields)


def managed_field_use_pipe(item):
    return "|".join([
        item["assemblyIdentity"], item["containingMethodSignature"], str(item["instructionOrdinal"]),
        item["opCode"], item["operation"], item["fieldSignature"],
    ])


def managed_call_site_pipe(item):
    return "|".join([
        item["assemblyIdentity"], item["containingMethodSignature"], str(item["instructionOrdinal"]),
        item["opCode"], item["calledMethodSignature"], bool_text(item["targetsMainActivity"]),
    ])


def managed_pinvoke_pipe(item):
    return "|".join([
        item["moduleName"], item["entryPoint"], item["callingConvention"], item["characterSet"],
        item["attributes"], item["assemblyIdentity"], item["methodSignature"],
    ])


def managed_interop_pipe(item):
    return "|".join([
        item["assemblyIdentity"], item["ownerSignature"], item["attributeType"],
        item["constructorSignature"], *item["argumentFingerprints"],
    ])


def support_field_use(item):
    return encode_fields([
        ("assembly", item["assemblyIdentity"]),
        ("method", item["containingMethodSignature"]),
        ("ordinal", item["instructionOrdinal"]),
        ("opcode", item["opCode"]),
        ("operation", item["operation"]),
        ("field", item["fieldSignature"]),
    ])


def support_call_site(item):
    return encode_fields([
        ("assembly", item["assemblyIdentity"]),
        ("method", item["containingMethodSignature"]),
        ("ordinal", item["instructionOrdinal"]),
        ("opcode", item["opCode"]),
        ("called", item["calledMethodSignature"]),
        ("targetsMainActivity", "true" if item["targetsMainActivity"] else "false"),
    ])


def support_pinvoke(item):
    return encode_fields([
        ("module", item["moduleName"]),
        ("entryPoint", item["entryPoint"]),
        ("callingConvention", item["callingConvention"]),
        ("characterSet", item["characterSet"]),
        ("attributes", item["attributes"]),
        ("assembly", item["assemblyIdentity"]),
        ("method", item["methodSignature"]),
    ])


def support_interop(item):
    fields = [
        ("assembly", item["assemblyIdentity"]),
        ("owner", item["ownerSignature"]),
        ("attributeType", item["attributeType"]),
        ("constructor", item["constructorSignature"]),
    ]
    fields.extend((f"argument[{index}]", value) for index, value in enumerate(item["argumentFingerprints"]))
    return encode_fields(fields)


def support_native(item):
    elf = item["elf"]
    return encode_fields([
        ("sourceLabel", item["sourceLabel"]),
        ("entryPath", item["entryPath"]),
        ("size", item["size"]),
        ("sha256", item["sha256"]),
        ("elfClass", elf["elfClass"]),
        ("dataEncoding", elf["dataEncoding"]),
        ("identVersion", elf["identVersion"]),
        ("osAbi", elf["osAbi"]),
        ("abiVersion", elf["abiVersion"]),
        ("objectType", elf["objectType"]),
        ("machine", elf["machine"]),
        ("flags", elf["flags"]),
    ])


def compute_managed_key(managed):
    counts = managed["fieldUseCounts"]
    canonical = CanonicalHash()
    canonical.add("schema", managed["schemaVersion"])
    canonical.add("target.identity", managed["targetAssemblyIdentity"])
    canonical.add("target.mvid", managed["targetModuleVersionId"])
    canonical.add("target.framework", managed["targetFramework"] if managed["targetFramework"] is not None else "<none>")
    canonical.add_array("target.reference", managed["assemblyReferences"])
    canonical.add("activity.base", managed["mainActivityBaseType"])
    canonical.add("activity.instance", managed["mainActivityInstanceFieldSignature"])
    canonical.add_array("activity.method", managed["mainActivityMethodSignatures"])
    canonical.add_array("activity.lifecycle", managed["lifecycleMethodSignatures"])
    canonical.add_array("activity.bootstrap", managed["bootstrapMethodSignatures"])
    canonical.add_array("field-use", [managed_field_use_pipe(item) for item in managed["fieldUses"]])
    canonical.add("field-use.count.read", str(counts["read"]))
    canonical.add("field-use.count.write", str(counts["write"]))
    canonical.add("field-use.count.address", str(counts["address"]))
    canonical.add("field-use.count.other", str(counts["other"]))
    canonical.add("field-use.count.total", str(counts["total"]))
    canonical.add_array("call-site", [managed_call_site_pipe(item) for item in managed["callSites"]])
    canonical.add("call-site.count", str(managed["callSiteCount"]))
    canonical.add_array("pinvoke", [managed_pinvoke_pipe(item) for item in managed["pInvokes"]])
    canonical.add_array("interop", [managed_interop_pipe(item) for item in managed["interopAttributes"]])
    return canonical.hexdigest()


def compute_support_key(managed, native_inventory):
    counts = managed["fieldUseCounts"]
    canonical = CanonicalHash()
    canonical.add("schema", SUPPORT_SCHEMA)
    canonical.add("managed.schema", managed["schemaVersion"])
    canonical.add("target.identity", managed["targetAssemblyIdentity"])
    canonical.add("target.mvid", managed["targetModuleVersionId"])
    canonical.add("target.framework", managed["targetFramework"] if managed["targetFramework"] is not None else "<none>")
    canonical.add_array("target.reference", sorted(managed["assemblyReferences"], key=ordinal_key))
    canonical.add("abi", native_inventory["selectedAbi"])
    canonical.add("activity.base", managed["mainActivityBaseType"])
    canonical.add("activity.instance", managed["mainActivityInstanceFieldSignature"])
    canonical.add_array("activity.method", sorted(managed["mainActivityMethodSignatures"], key=ordinal_key))
    canonical.add_array("activity.lifecycle", sorted(managed["lifecycleMethodSignatures"], key=ordinal_key))
    canonical.add_array("activity.bootstrap", sorted(managed["bootstrapMethodSignatures"], key=ordinal_key))
    canonical.add_array("field-use", sorted((support_field_use(item) for item in managed["fieldUses"]), key=ordinal_key))
    canonical.add("field-use.count.read", str(counts["read"]))
    canonical.add("field-use.count.write", str(counts["write"]))
    canonical.add("field-use.count.address", str(counts["address"]))
    canonical.add("field-use.count.other", str(counts["other"]))
    canonical.add("field-use.count.total", str(counts["total"]))
    canonical.add_array("call-site", sorted((support_call_site(item) for item in managed["callSites"]), key=ordinal_key))
    canonical.add("call-site.count", str(managed["callSiteCount"]))
    canonical.add_array("pinvoke", sorted((support_pinvoke(item) for item in managed["pInvokes"]), key=ordinal_key))
    canonical.add_array("interop", sorted((support_interop(item) for item in managed["interopAttributes"]), key=ordinal_key))
    entries = sorted(
        native_inventory["entries"],
        key=lambda item: (
            ordinal_key(item["entryPath"]), ordinal_key(item["sourceLabel"]),
            ordinal_key(item["sha256"]), item["size"],
        ),
    )
    canonical.add_array("native", [support_native(item) for item in entries])
    return canonical.hexdigest()


probe = load(PROBE_PATH)
workspace = load(WORKSPACE_PATH)
discovery = load(DISCOVERY_PATH)
reject_sensitive(probe)
require_fields(probe, {
    "format", "formatVersion", "generatedAtUtc", "operation", "packageName", "selectedAbi",
    "status", "workspaceKey", "managedEvidenceKey", "supportKey", "managedEvidence",
    "nativeInventory", "diagnostics",
}, "$")
require(probe["format"] == PROBE_FORMAT, "format is incorrect")
require(probe["formatVersion"] == 1, "formatVersion must equal 1")
require_timestamp(probe["generatedAtUtc"], "$.generatedAtUtc")
require(probe["operation"] == "metadata-only", "operation must be metadata-only")
require(probe["packageName"] == PLAY_PACKAGE, "packageName must be the tested Play package")
require(probe["selectedAbi"] == SELECTED_ABI, "selectedAbi must be arm64-v8a")
require(probe["status"] == "Succeeded", "Gate 0 status must be Succeeded")
for field in ("workspaceKey", "managedEvidenceKey", "supportKey"):
    require(isinstance(probe[field], str) and HEX64.fullmatch(probe[field]), f"{field} must be canonical SHA-256")
require(probe["managedEvidenceKey"] != probe["supportKey"], "managed-only and composite support keys must differ")
if EXPECTED_SUPPORT_KEY:
    require(probe["supportKey"] == EXPECTED_SUPPORT_KEY, "supportKey changed between runs")

require_fields(workspace, {
    "formatVersion", "generatedAtUtc", "packageName", "status", "workspaceKey", "statistics",
    "metrics", "progressStages", "diagnostics",
}, "workspace")
require(workspace["formatVersion"] == 2, "workspace formatVersion must equal 2")
require(workspace["packageName"] == PLAY_PACKAGE, "workspace packageName mismatch")
require(workspace["status"] == EXPECTED_WORKSPACE_STATUS,
        f"workspace status must be {EXPECTED_WORKSPACE_STATUS}")
require(workspace["workspaceKey"] == probe["workspaceKey"], "probe/workspace key mismatch")
require_timestamp(workspace["generatedAtUtc"], "workspace.generatedAtUtc")

require(isinstance(discovery.get("candidates"), list), "discovery candidates must be an array")
play_candidates = [item for item in discovery["candidates"] if isinstance(item, dict) and item.get("packageName") == PLAY_PACKAGE]
require(len(play_candidates) == 1, "discovery report must contain exactly one Play candidate")
candidate = play_candidates[0]
require(candidate.get("versionName") == EXPECTED_GAME_VERSION, "installed game version is outside the frozen Gate 0 baseline")
require(candidate.get("longVersionCode") == int(EXPECTED_GAME_VERSION_CODE), "installed game versionCode is outside the frozen Gate 0 baseline")
require(candidate.get("selectedAbi") == SELECTED_ABI, "discovery selectedAbi mismatch")
require(candidate.get("allowsCodeExecution") is True, "discovery certificate policy does not allow Gate 0 inspection")
require(candidate.get("gameCertificateStatus") in {"KnownTested", "KnownTestedAfterRotation"},
        "discovery certificate status is not trusted")
apk_sources = candidate.get("apkSources")
require(isinstance(apk_sources, list) and apk_sources, "discovery APK sources are missing")
source_labels = {source.get("label") for source in apk_sources if isinstance(source, dict)}
require(len(source_labels) == len(apk_sources) and all(isinstance(label, str) and label for label in source_labels),
        "discovery APK source labels are invalid")

managed = probe["managedEvidence"]
require_fields(managed, {
    "schemaVersion", "targetAssemblyIdentity", "targetModuleVersionId", "targetFramework",
    "assemblyReferences", "mainActivityBaseType", "mainActivityInstanceFieldSignature",
    "mainActivityMethodSignatures", "lifecycleMethodSignatures", "bootstrapMethodSignatures",
    "fieldUses", "fieldUseCounts", "callSites", "callSiteCount", "pInvokes", "interopAttributes",
}, "$.managedEvidence")
require(managed["schemaVersion"] == PROBE_SCHEMA, "managed evidence schema is incorrect")
require_string(managed["targetAssemblyIdentity"], "managed.targetAssemblyIdentity")
require(managed["targetAssemblyIdentity"].startswith("StardewValley, Version="),
        "target assembly identity is not StardewValley")
require_string(managed["targetModuleVersionId"], "managed.targetModuleVersionId", 64)
try:
    parsed_mvid = uuid.UUID(managed["targetModuleVersionId"])
except (ValueError, AttributeError):
    fail("targetModuleVersionId is not a UUID")
require(str(parsed_mvid) == managed["targetModuleVersionId"], "targetModuleVersionId is not canonical lowercase D format")
require(managed["targetFramework"] is None or isinstance(managed["targetFramework"], str),
        "targetFramework must be null or a string")
references = validate_string_array(managed["assemblyReferences"], "managed.assemblyReferences", False, True)
require(any(reference.startswith("Mono.Android, Version=") for reference in references),
        "managed references omit Mono.Android")
require(any(reference.startswith("MonoGame.Framework, Version=") for reference in references),
        "managed references omit MonoGame.Framework")
require_string(managed["mainActivityBaseType"], "managed.mainActivityBaseType")
require_string(managed["mainActivityInstanceFieldSignature"], "managed.mainActivityInstanceFieldSignature")
require("StardewValley.MainActivity::instance" in managed["mainActivityInstanceFieldSignature"],
        "MainActivity.instance field signature is missing")
methods = validate_string_array(managed["mainActivityMethodSignatures"], "managed.mainActivityMethodSignatures", False, True)
lifecycle = validate_string_array(managed["lifecycleMethodSignatures"], "managed.lifecycleMethodSignatures", False, True)
validate_string_array(managed["bootstrapMethodSignatures"], "managed.bootstrapMethodSignatures", True, True)
for method_name in (".ctor", "OnCreate", "OnResume", "OnPause", "OnDestroy"):
    require(any(f"::{method_name}(" in signature for signature in lifecycle),
            f"lifecycle evidence omits {method_name}")
require(set(lifecycle) <= set(methods), "lifecycle signatures must be a subset of MainActivity methods")

field_use_fields = {
    "assemblyIdentity", "containingMethodSignature", "instructionOrdinal", "opCode", "operation", "fieldSignature",
}
field_uses = managed["fieldUses"]
require(isinstance(field_uses, list) and field_uses, "fieldUses must be a non-empty array")
for index, item in enumerate(field_uses):
    location = f"managed.fieldUses[{index}]"
    require_fields(item, field_use_fields, location)
    for field in ("assemblyIdentity", "containingMethodSignature", "opCode", "fieldSignature"):
        require_string(item[field], f"{location}.{field}")
    require_int(item["instructionOrdinal"], f"{location}.instructionOrdinal")
    require(item["operation"] in FIELD_OPERATIONS, f"{location}.operation is invalid")
counts = managed["fieldUseCounts"]
require_fields(counts, {"read", "write", "address", "other", "total"}, "managed.fieldUseCounts")
for field in counts:
    require_int(counts[field], f"managed.fieldUseCounts.{field}")
computed_counts = {operation.casefold(): sum(item["operation"] == operation for item in field_uses) for operation in FIELD_OPERATIONS}
require(counts["read"] == computed_counts["read"], "field use read count mismatch")
require(counts["write"] == computed_counts["write"], "field use write count mismatch")
require(counts["address"] == computed_counts["address"], "field use address count mismatch")
require(counts["other"] == computed_counts["other"], "field use other count mismatch")
require(counts["total"] == len(field_uses) == sum(counts[field] for field in ("read", "write", "address", "other")),
        "field use total is inconsistent")

call_site_fields = {
    "assemblyIdentity", "containingMethodSignature", "instructionOrdinal", "opCode",
    "calledMethodSignature", "targetsMainActivity",
}
call_sites = managed["callSites"]
require(isinstance(call_sites, list) and call_sites, "callSites must be a non-empty array")
for index, item in enumerate(call_sites):
    location = f"managed.callSites[{index}]"
    require_fields(item, call_site_fields, location)
    for field in ("assemblyIdentity", "containingMethodSignature", "opCode", "calledMethodSignature"):
        require_string(item[field], f"{location}.{field}")
    require_int(item["instructionOrdinal"], f"{location}.instructionOrdinal")
    require(isinstance(item["targetsMainActivity"], bool), f"{location}.targetsMainActivity must be boolean")
require_int(managed["callSiteCount"], "managed.callSiteCount", 1)
require(managed["callSiteCount"] == len(call_sites), "callSiteCount mismatch")

pinvoke_fields = {
    "assemblyIdentity", "methodSignature", "moduleName", "entryPoint",
    "callingConvention", "characterSet", "attributes",
}
pinvokes = managed["pInvokes"]
require(isinstance(pinvokes, list) and pinvokes, "pInvokes must be a non-empty array")
for index, item in enumerate(pinvokes):
    location = f"managed.pInvokes[{index}]"
    require_fields(item, pinvoke_fields, location)
    for field in pinvoke_fields:
        require_string(item[field], f"{location}.{field}")

interop_fields = {
    "assemblyIdentity", "ownerSignature", "attributeType", "constructorSignature", "argumentFingerprints",
}
interop = managed["interopAttributes"]
require(isinstance(interop, list) and interop, "interopAttributes must be a non-empty array")
for index, item in enumerate(interop):
    location = f"managed.interopAttributes[{index}]"
    require_fields(item, interop_fields, location)
    for field in ("assemblyIdentity", "ownerSignature", "attributeType", "constructorSignature"):
        require_string(item[field], f"{location}.{field}")
    validate_string_array(item["argumentFingerprints"], f"{location}.argumentFingerprints")

native = probe["nativeInventory"]
require_fields(native, {"selectedAbi", "entryCount", "totalBytes", "entries"}, "$.nativeInventory")
require(native["selectedAbi"] == SELECTED_ABI, "native selectedAbi mismatch")
require_int(native["entryCount"], "native.entryCount", 1)
require_int(native["totalBytes"], "native.totalBytes", 1)
entries = native["entries"]
require(isinstance(entries, list) and len(entries) == native["entryCount"], "native entryCount mismatch")
native_fields = {"sourceLabel", "entryPath", "size", "compressedSize", "sha256", "elf"}
elf_fields = {"elfClass", "dataEncoding", "identVersion", "osAbi", "abiVersion", "objectType", "machine", "flags"}
source_paths = set()
source_paths_casefold = set()
global_paths = {}
for index, item in enumerate(entries):
    location = f"native.entries[{index}]"
    require_fields(item, native_fields, location)
    label = require_string(item["sourceLabel"], f"{location}.sourceLabel", 128)
    require(label in source_labels, f"{location}.sourceLabel is not in the discovery report")
    require(unicodedata.normalize("NFC", label) == label, f"{location}.sourceLabel is not NFC")
    require("/" not in label and "\\" not in label and "|" not in label and "\x00" not in label and
            not any(ord(character) < 32 for character in label),
            f"{location}.sourceLabel is not canonical")
    path = require_string(item["entryPath"], f"{location}.entryPath", 1024)
    require(unicodedata.normalize("NFC", path) == path, f"{location}.entryPath is not NFC")
    parts = path.split("/")
    require(len(parts) == 3 and parts[:2] == ["lib", SELECTED_ABI], f"{location}.entryPath has an invalid selected-ABI shape")
    require(LIBRARY_NAME.fullmatch(parts[2]) is not None, f"{location}.entryPath has an invalid library filename")
    size = require_int(item["size"], f"{location}.size", 1)
    require_int(item["compressedSize"], f"{location}.compressedSize", 1)
    require(isinstance(item["sha256"], str) and HEX64.fullmatch(item["sha256"]), f"{location}.sha256 is invalid")
    source_path = (label, path)
    source_path_folded = (label.casefold(), path.casefold())
    require(source_path not in source_paths, f"{location} duplicates a source/path identity")
    require(source_path_folded not in source_paths_casefold,
            f"{location} collides by source/path case")
    source_paths.add(source_path)
    source_paths_casefold.add(source_path_folded)
    folded_path = path.casefold()
    if folded_path in global_paths:
        require(global_paths[folded_path] == path, f"{location}.entryPath collides by case across sources")
    else:
        global_paths[folded_path] = path
    elf = item["elf"]
    require_fields(elf, elf_fields, f"{location}.elf")
    for field in elf_fields:
        require_int(elf[field], f"{location}.elf.{field}", 0, 0xFFFFFFFF)
    require(elf["elfClass"] == 2 and elf["dataEncoding"] == 1 and elf["identVersion"] == 1,
            f"{location}.elf is not ELF64 little-endian version 1")
    require(elf["machine"] == 183 and elf["objectType"] in {2, 3},
            f"{location}.elf is not an ARM64 executable/shared object")
require(native["totalBytes"] == sum(item["size"] for item in entries), "native totalBytes mismatch")
expected_native_order = sorted(
    entries,
    key=lambda item: (
        ordinal_key(item["entryPath"]), ordinal_key(item["sha256"]),
        item["size"], ordinal_key(item["sourceLabel"]),
    ),
)
require(entries == expected_native_order, "native entries are not in deterministic inventory order")
require(any(pathlib.PurePosixPath(item["entryPath"]).name.casefold().startswith("libassemblies.") for item in entries),
        "native inventory omits the selected-ABI AssemblyStore entry")

managed_key = compute_managed_key(managed)
support_key = compute_support_key(managed, native)
require(managed_key == probe["managedEvidenceKey"], "managedEvidenceKey does not match independently canonicalized evidence")
require(support_key == probe["supportKey"], "supportKey does not match independently canonicalized managed/native evidence")

diagnostics = probe["diagnostics"]
require(isinstance(diagnostics, list) and diagnostics, "diagnostics must be a non-empty array")
required_codes = {
    "gamehost_probe_succeeded",
    "gamehost_probe_native_succeeded",
    "gamehost_probe_gate0_succeeded",
}
codes = set()
for index, item in enumerate(diagnostics):
    location = f"diagnostics[{index}]"
    require_fields(item, {"timestampUtc", "code", "severity", "message"}, location)
    require_timestamp(item["timestampUtc"], f"{location}.timestampUtc")
    code = require_string(item["code"], f"{location}.code", 256)
    require(code.startswith("gamehost_"), f"{location}.code has an invalid prefix")
    require(item["severity"] in SEVERITIES, f"{location}.severity is invalid")
    require_string(item["message"], f"{location}.message")
    codes.add(code)
require(required_codes <= codes, "successful Gate 0 diagnostics omit a required trust/probe stage")
require(not any(item["severity"] == "Error" for item in diagnostics), "successful Gate 0 report contains an error diagnostic")

summary = {
    "workspaceKey": probe["workspaceKey"],
    "managedEvidenceKey": probe["managedEvidenceKey"],
    "supportKey": probe["supportKey"],
    "targetAssemblyIdentity": managed["targetAssemblyIdentity"],
    "targetMvid": managed["targetModuleVersionId"],
    "fieldUses": counts["total"],
    "callSites": managed["callSiteCount"],
    "pInvokes": len(pinvokes),
    "interopAttributes": len(interop),
    "nativeEntries": native["entryCount"],
    "nativeBytes": native["totalBytes"],
}
print(probe["supportKey"])
print(json.dumps(summary, sort_keys=True, separators=(",", ":")))
PY
}

compare_deterministic_evidence() {
  python3 - "$1" "$2" <<'PY'
import json
import sys

with open(sys.argv[1], "r", encoding="utf-8") as stream:
    first = json.load(stream)
with open(sys.argv[2], "r", encoding="utf-8") as stream:
    second = json.load(stream)
for field in ("workspaceKey", "managedEvidenceKey", "supportKey", "managedEvidence", "nativeInventory"):
    if first.get(field) != second.get(field):
        raise SystemExit(f"Gate 0 deterministic evidence changed between runs: {field}")
PY
}

run_probe() {
  local label="$1"
  local expected_workspace_status="$2"
  local expected_support_key="$3"
  local probe_destination="$4"
  local workspace_destination="$5"
  local discovery_destination="$6"

  "${adb_device[@]}" shell am force-stop "$package"
  "${adb_device[@]}" shell run-as "$package" rm -f \
    files/reports/game-discovery-latest.json \
    files/reports/game-workspace-latest.json \
    files/reports/gamehost-probe-latest.json >/dev/null 2>&1 || true
  "${adb_device[@]}" logcat -c
  local started=$SECONDS
  "${adb_device[@]}" shell am start -W -n "$component" >/dev/null
  local pid
  pid="$(wait_for_pid)" || { echo "Could not resolve the $label JunimoGate App PID." >&2; exit 6; }
  printf 'Waiting up to %s seconds for the %s Gate 0 report...\n' "$timeout_seconds" "$label" >&2
  if ! wait_for_probe_report "$probe_destination"; then
    echo "JunimoGate App did not produce a valid $label Gate 0 report within $timeout_seconds seconds." >&2
    exit 6
  fi
  read_private_json files/reports/game-workspace-latest.json "$workspace_destination"
  read_private_json files/reports/game-discovery-latest.json "$discovery_destination"
  "${adb_device[@]}" logcat -d --pid="$pid" >>"$logcat_file"

  local validation
  if ! validation="$(validate_probe_report \
      "$probe_destination" "$workspace_destination" "$discovery_destination" \
      "$expected_workspace_status" "$expected_support_key")"; then
    exit 7
  fi
  mapfile -t values <<<"$validation"
  [[ ${#values[@]} -eq 2 ]] || { echo "$label Gate 0 validator returned incomplete output." >&2; exit 7; }
  printf '%s\n' "${values[0]}"
  printf '%s\n' "${values[1]}"
  printf '%s Gate 0 run passed in %ss end-to-end.\n' "$label" "$((SECONDS - started))" >&2
}

printf 'Running first Gate 0 acceptance from clean JunimoGate app data...\n'
if ! first_validation="$(run_probe first Built "" "$first_probe" "$first_workspace" "$first_discovery")"; then
  exit 7
fi
mapfile -t first_values <<<"$first_validation"
[[ ${#first_values[@]} -eq 2 ]] || { echo "First Gate 0 run returned incomplete evidence." >&2; exit 7; }
first_support_key="${first_values[0]}"
first_summary="${first_values[1]}"

printf 'Running second Gate 0 acceptance against the immutable M4 CacheHit...\n'
if ! second_validation="$(run_probe second CacheHit "$first_support_key" "$second_probe" "$second_workspace" "$second_discovery")"; then
  exit 8
fi
mapfile -t second_values <<<"$second_validation"
[[ ${#second_values[@]} -eq 2 ]] || { echo "Second Gate 0 run returned incomplete evidence." >&2; exit 8; }
second_support_key="${second_values[0]}"
second_summary="${second_values[1]}"
[[ "$second_support_key" == "$first_support_key" ]] || { echo "Gate 0 supportKey changed between runs." >&2; exit 8; }
compare_deterministic_evidence "$first_probe" "$second_probe"

if grep -E 'FATAL EXCEPTION|AndroidRuntime[^:]*:.*FATAL' "$logcat_file" >/dev/null; then
  echo "JunimoGate App PID-filtered logcat contains a FATAL EXCEPTION or AndroidRuntime crash." >&2
  grep -E 'FATAL EXCEPTION|AndroidRuntime[^:]*:.*FATAL' "$logcat_file" >&2 || true
  exit 9
fi

artifact_dir="$root/artifacts/android"
mkdir -p "$artifact_dir"
artifact="$artifact_dir/gamehost-probe-debug.json"
cp "$second_probe" "$artifact"

printf 'Gate 0 metadata-only device verification passed.\n'
printf 'First evidence:  %s\n' "$first_summary"
printf 'Second evidence: %s\n' "$second_summary"
printf 'Deterministic support key: %s\n' "$first_support_key"
printf 'Only redacted JSON and PID-filtered logcat were read; no APK, DLL, Content, or native payload was copied from the device.\n'
printf 'Redacted Gate 0 report: %s\n' "$artifact"
