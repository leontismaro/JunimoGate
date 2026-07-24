#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=android-toolchain-versions.sh
source "$root/build/android-toolchain-versions.sh"
# shellcheck source=android-env.sh
source "$root/build/android-env.sh"

export JUNIMOGATE_DOTNET_SDK_VERSION="$DOTNET_SDK_VERSION"
export JUNIMOGATE_JDK_VERSION="$JDK_VERSION"
export JUNIMOGATE_ANDROID_CLI_VERSION="$ANDROID_COMMAND_LINE_TOOLS_VERSION"
export JUNIMOGATE_ANDROID_PLATFORM_VERSION="$ANDROID_PLATFORM_VERSION"
export JUNIMOGATE_ANDROID_BUILD_TOOLS_VERSION="$ANDROID_BUILD_TOOLS_VERSION"

output="${1:-$root/artifacts/android/environment.json}"
python3 "$root/build/report-android-environment.py" --output "$output"
