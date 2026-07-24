#!/usr/bin/env bash
# Source this file to select the project-local .NET Android toolchain.

_junimogate_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=android-toolchain-versions.sh
source "$_junimogate_root/build/android-toolchain-versions.sh"

export JUNIMOGATE_TOOLCHAINS_DIR="${JUNIMOGATE_TOOLCHAINS_DIR:-$_junimogate_root/.toolchains}"
export DOTNET_ROOT="$JUNIMOGATE_TOOLCHAINS_DIR/dotnet"
export JAVA_HOME="$JUNIMOGATE_TOOLCHAINS_DIR/jdk-17"
export ANDROID_SDK_ROOT="$JUNIMOGATE_TOOLCHAINS_DIR/android-sdk"
export ANDROID_HOME="$ANDROID_SDK_ROOT"
export ANDROID_USER_HOME="$JUNIMOGATE_TOOLCHAINS_DIR/android-user"
export DOTNET_CLI_HOME="$JUNIMOGATE_TOOLCHAINS_DIR/dotnet-home"
export NUGET_PACKAGES="$JUNIMOGATE_TOOLCHAINS_DIR/nuget-packages"
export NUGET_HTTP_CACHE_PATH="$JUNIMOGATE_TOOLCHAINS_DIR/nuget-http-cache"
export NUGET_XMLDOC_MODE=skip
export DOTNET_MULTILEVEL_LOOKUP=0
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
export MSBUILDDISABLENODEREUSE=1

_android_cli="$ANDROID_SDK_ROOT/cmdline-tools/$ANDROID_COMMAND_LINE_TOOLS_VERSION/bin"
export PATH="$DOTNET_ROOT:$JAVA_HOME/bin:$_android_cli:$ANDROID_SDK_ROOT/platform-tools:$PATH"

_junimogate_missing=()
[[ -x "$DOTNET_ROOT/dotnet" ]] || _junimogate_missing+=(".NET SDK $DOTNET_SDK_VERSION")
[[ -x "$JAVA_HOME/bin/java" ]] || _junimogate_missing+=("JDK $JDK_VERSION")
[[ -x "$_android_cli/sdkmanager" ]] || _junimogate_missing+=("Android command-line tools $ANDROID_COMMAND_LINE_TOOLS_VERSION")
[[ -d "$ANDROID_SDK_ROOT/platforms/android-$ANDROID_PLATFORM_VERSION" ]] || _junimogate_missing+=("Android platform $ANDROID_PLATFORM_VERSION")
[[ -d "$ANDROID_SDK_ROOT/build-tools/$ANDROID_BUILD_TOOLS_VERSION" ]] || _junimogate_missing+=("Android build-tools $ANDROID_BUILD_TOOLS_VERSION")

if ((${#_junimogate_missing[@]})); then
  printf 'JunimoGate Android toolchain is incomplete:\n' >&2
  printf '  - %s\n' "${_junimogate_missing[@]}" >&2
  printf 'Run %s/build/bootstrap-android.sh first.\n' "$_junimogate_root" >&2
  unset _junimogate_root _android_cli _junimogate_missing
  return 1 2>/dev/null || exit 1
fi

unset _junimogate_root _android_cli _junimogate_missing
