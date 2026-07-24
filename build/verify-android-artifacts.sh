#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=android-toolchain-versions.sh
source "$root/build/android-toolchain-versions.sh"
# shellcheck source=android-env.sh
source "$root/build/android-env.sh"

export JUNIMOGATE_ROOT="$root"
export JUNIMOGATE_ANDROID_BUILD_TOOLS_VERSION="$ANDROID_BUILD_TOOLS_VERSION"
export JUNIMOGATE_APK_REPORT="${1:-$root/artifacts/android/apk-verification.json}"
python3 "$root/build/verify-android-artifacts.py"
