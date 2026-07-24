#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=android-toolchain-versions.sh
source "$root/build/android-toolchain-versions.sh"

toolchains="${JUNIMOGATE_TOOLCHAINS_DIR:-$root/.toolchains}"
downloads="$toolchains/downloads"
proxy_url="${JUNIMOGATE_PROXY_URL:-}"
if [[ -n "$proxy_url" ]]; then
  export HTTP_PROXY="$proxy_url"
  export HTTPS_PROXY="$proxy_url"
  export ALL_PROXY="$proxy_url"
  export http_proxy="$proxy_url"
  export https_proxy="$proxy_url"
  export all_proxy="$proxy_url"
fi
dotnet_root="$toolchains/dotnet"
jdk_root="$toolchains/jdk-17"
android_sdk="$toolchains/android-sdk"
android_cli_root="$android_sdk/cmdline-tools/$ANDROID_COMMAND_LINE_TOOLS_VERSION"

mkdir -p "$downloads" "$toolchains/android-user" "$toolchains/dotnet-home"

_staging=()
cleanup() {
  if ((${#_staging[@]})); then
    python3 - "${_staging[@]}" <<'PY'
import pathlib, shutil, sys
for raw in sys.argv[1:]:
    path = pathlib.Path(raw)
    if path.exists():
        shutil.rmtree(path)
PY
  fi
}
trap cleanup EXIT

verify_file() {
  local algorithm="$1"
  local expected="$2"
  local path="$3"
  local actual
  actual="$("${algorithm}sum" "$path" | awk '{print $1}')"
  if [[ "$actual" != "$expected" ]]; then
    printf 'Checksum mismatch for %s\nexpected: %s\nactual:   %s\n' "$path" "$expected" "$actual" >&2
    exit 1
  fi
}

download() {
  local url="$1"
  local path="$2"
  if [[ ! -f "$path" ]]; then
    printf 'Downloading %s\n' "$url"
    curl --fail --location --retry 4 --retry-delay 2 --continue-at - --output "$path.part" "$url"
    mv "$path.part" "$path"
  fi
}

install_dotnet() {
  local archive="$downloads/dotnet-sdk-$DOTNET_SDK_VERSION-linux-x64.tar.gz"
  if [[ -x "$dotnet_root/dotnet" ]] && "$dotnet_root/dotnet" --list-sdks | grep -q "^$DOTNET_SDK_VERSION "; then
    return
  fi
  if [[ -e "$dotnet_root" ]]; then
    printf 'Incomplete or unexpected local .NET installation: %s\nRemove that ignored directory and rerun.\n' "$dotnet_root" >&2
    exit 1
  fi
  download "$DOTNET_SDK_URL" "$archive"
  verify_file sha512 "$DOTNET_SDK_SHA512" "$archive"
  local stage="$toolchains/.install-dotnet.$$"
  _staging+=("$stage")
  mkdir "$stage"
  tar -xzf "$archive" -C "$stage"
  mv "$stage" "$dotnet_root"
}

install_jdk() {
  local archive="$downloads/$JDK_ARCHIVE_NAME"
  if [[ -x "$jdk_root/bin/java" ]] && "$jdk_root/bin/java" -version 2>&1 | grep -q '17\.0\.16'; then
    return
  fi
  if [[ -e "$jdk_root" ]]; then
    printf 'Incomplete or unexpected local JDK installation: %s\nRemove that ignored directory and rerun.\n' "$jdk_root" >&2
    exit 1
  fi
  download "$JDK_URL" "$archive"
  verify_file sha256 "$JDK_SHA256" "$archive"
  local stage="$toolchains/.install-jdk.$$"
  _staging+=("$stage")
  mkdir "$stage"
  tar -xzf "$archive" -C "$stage"
  local extracted
  extracted="$(find "$stage" -mindepth 1 -maxdepth 1 -type d -print -quit)"
  [[ -n "$extracted" ]] || { echo 'JDK archive did not contain a root directory.' >&2; exit 1; }
  mv "$extracted" "$jdk_root"
}

install_android_cli() {
  local archive="$downloads/$ANDROID_COMMAND_LINE_TOOLS_ARCHIVE"
  if [[ -x "$android_cli_root/bin/sdkmanager" ]]; then
    return
  fi
  if [[ -e "$android_cli_root" ]]; then
    printf 'Incomplete Android command-line tools installation: %s\n' "$android_cli_root" >&2
    exit 1
  fi
  download "$ANDROID_COMMAND_LINE_TOOLS_URL" "$archive"
  verify_file sha1 "$ANDROID_COMMAND_LINE_TOOLS_SHA1" "$archive"
  local stage="$toolchains/.install-android-cli.$$"
  _staging+=("$stage")
  mkdir "$stage"
  unzip -q "$archive" -d "$stage"
  [[ -d "$stage/cmdline-tools" ]] || { echo 'Unexpected Android command-line tools archive layout.' >&2; exit 1; }
  mkdir -p "$android_sdk/cmdline-tools"
  mv "$stage/cmdline-tools" "$android_cli_root"
}

install_dotnet
install_jdk
install_android_cli

export DOTNET_ROOT="$dotnet_root"
export JAVA_HOME="$jdk_root"
export ANDROID_SDK_ROOT="$android_sdk"
export ANDROID_HOME="$android_sdk"
export ANDROID_USER_HOME="$toolchains/android-user"
export DOTNET_CLI_HOME="$toolchains/dotnet-home"
export DOTNET_MULTILEVEL_LOOKUP=0
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export PATH="$DOTNET_ROOT:$JAVA_HOME/bin:$android_cli_root/bin:$ANDROID_SDK_ROOT/platform-tools:$PATH"

sdkmanager="$android_cli_root/bin/sdkmanager"
sdkmanager_proxy_args=()
if [[ -n "$proxy_url" ]]; then
  read -r sdk_proxy_type sdk_proxy_host sdk_proxy_port < <(python3 - "$proxy_url" <<'PY'
import sys
from urllib.parse import urlparse
value = urlparse(sys.argv[1])
if not value.hostname or not value.port:
    raise SystemExit("JUNIMOGATE_PROXY_URL must include scheme, host, and port")
kind = "socks" if value.scheme.startswith("socks") else "http"
print(kind, value.hostname, value.port)
PY
)
  sdkmanager_proxy_args+=("--proxy=$sdk_proxy_type" "--proxy_host=$sdk_proxy_host" "--proxy_port=$sdk_proxy_port")
fi

printf 'Accepting Android SDK licenses for the project-local SDK...\n'
set +o pipefail
yes | "$sdkmanager" --sdk_root="$android_sdk" "${sdkmanager_proxy_args[@]}" --licenses >/dev/null
license_status=${PIPESTATUS[1]}
set -o pipefail
[[ "$license_status" -eq 0 ]] || exit "$license_status"

"$sdkmanager" --sdk_root="$android_sdk" "${sdkmanager_proxy_args[@]}" \
  "platform-tools" \
  "platforms;android-$ANDROID_PLATFORM_VERSION" \
  "build-tools;$ANDROID_BUILD_TOOLS_VERSION"

JUNIMOGATE_TOOLCHAINS_DIR="$toolchains" \
JUNIMOGATE_PROXY_URL="$proxy_url" \
  "$root/build/install-android-workload.sh"

printf '\nProject-local Android toolchain is ready.\n'
printf '  .NET SDK:      %s\n' "$DOTNET_SDK_VERSION"
printf '  JDK:           %s\n' "$JDK_VERSION"
printf '  Android API:   %s\n' "$ANDROID_PLATFORM_VERSION"
printf '  Build tools:   %s\n' "$ANDROID_BUILD_TOOLS_VERSION"
printf '  Root:          %s\n' "$toolchains"
printf '\nUse: source %q\n' "$root/build/android-env.sh"

if [[ -x "$root/build/report-android-environment.sh" ]]; then
  "$root/build/report-android-environment.sh" >/dev/null
  printf 'Environment report: %s\n' "$root/artifacts/android/environment.json"
fi
