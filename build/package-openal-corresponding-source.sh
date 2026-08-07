#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=monogame-android-versions.sh
source "$root/build/monogame-android-versions.sh"

output_dir="${1:-$root/artifacts/corresponding-source}"
cache_dir="$root/.toolchains/source-cache/openal"
proxy_url="${JUNIMOGATE_PROXY_URL:-}"
mkdir -p "$output_dir" "$cache_dir"

sha256_file() {
  sha256sum "$1" | awk '{print $1}'
}

download_verified() {
  local url="$1"
  local destination="$2"
  local expected_sha256="$3"
  local label="$4"

  if [[ -f "$destination" && "$(sha256_file "$destination")" == "$expected_sha256" ]]; then
    return
  fi

  rm -f "$destination" "$destination.partial"
  local curl_args=(
    --fail --location --silent --show-error
    --retry 5 --retry-delay 2 --retry-all-errors
    --connect-timeout 30
    --output "$destination.partial"
  )
  if [[ -n "$proxy_url" ]]; then
    curl_args+=(--proxy "$proxy_url")
  fi

  printf 'Downloading %s source...\n' "$label"
  curl "${curl_args[@]}" "$url"
  local actual_sha256
  actual_sha256="$(sha256_file "$destination.partial")"
  if [[ "$actual_sha256" != "$expected_sha256" ]]; then
    printf '%s source SHA-256 mismatch: expected %s, got %s\n' \
      "$label" "$expected_sha256" "$actual_sha256" >&2
    exit 2
  fi
  mv "$destination.partial" "$destination"
}

provider_archive="$cache_dir/provider-$JUNIMOGATE_OPENAL_PROVIDER_COMMIT.tar.gz"
openal_archive="$cache_dir/openal-soft-$JUNIMOGATE_OPENAL_SOURCE_COMMIT.tar.gz"
buildscripts_archive="$cache_dir/buildscripts-$JUNIMOGATE_OPENAL_BUILDSCRIPTS_COMMIT.tar.gz"

download_verified \
  "https://codeload.github.com/MonoGame/MonoGame.Library.OpenAL/tar.gz/$JUNIMOGATE_OPENAL_PROVIDER_COMMIT" \
  "$provider_archive" \
  "$JUNIMOGATE_OPENAL_PROVIDER_ARCHIVE_SHA256" \
  "MonoGame.Library.OpenAL"
download_verified \
  "https://codeload.github.com/kcat/openal-soft/tar.gz/$JUNIMOGATE_OPENAL_SOURCE_COMMIT" \
  "$openal_archive" \
  "$JUNIMOGATE_OPENAL_SOURCE_ARCHIVE_SHA256" \
  "OpenAL Soft"
download_verified \
  "https://codeload.github.com/MonoGame/MonoGame.Library.BuildScripts/tar.gz/$JUNIMOGATE_OPENAL_BUILDSCRIPTS_COMMIT" \
  "$buildscripts_archive" \
  "$JUNIMOGATE_OPENAL_BUILDSCRIPTS_ARCHIVE_SHA256" \
  "MonoGame library build scripts"

working_dir="$(mktemp -d "${TMPDIR:-/tmp}/junimogate-openal-source.XXXXXX")"
trap 'rm -rf "$working_dir"' EXIT
bundle_root="$working_dir/MonoGame.Library.OpenAL-$JUNIMOGATE_OPENAL_PACKAGE_VERSION"
mkdir -p "$bundle_root/openal-soft" "$bundle_root/buildscripts"
tar -xzf "$provider_archive" -C "$bundle_root" --strip-components=1
tar -xzf "$openal_archive" -C "$bundle_root/openal-soft" --strip-components=1
tar -xzf "$buildscripts_archive" -C "$bundle_root/buildscripts" --strip-components=1

cat > "$bundle_root/SOURCE-PROVENANCE.md" <<EOF
# OpenAL corresponding source

This archive corresponds to the OpenAL Soft binary distributed by JunimoGate.

- Binary package: MonoGame.Library.OpenAL $JUNIMOGATE_OPENAL_PACKAGE_VERSION
- Binary package SHA-256: $JUNIMOGATE_OPENAL_PACKAGE_SHA256
- Provider repository: $JUNIMOGATE_OPENAL_PROVIDER_REPOSITORY
- Provider commit: $JUNIMOGATE_OPENAL_PROVIDER_COMMIT
- OpenAL Soft repository: $JUNIMOGATE_OPENAL_SOURCE_REPOSITORY
- OpenAL Soft commit: $JUNIMOGATE_OPENAL_SOURCE_COMMIT (release 1.24.3)
- Build scripts repository: $JUNIMOGATE_OPENAL_BUILDSCRIPTS_REPOSITORY
- Build scripts commit: $JUNIMOGATE_OPENAL_BUILDSCRIPTS_COMMIT
- Android ARM64 binary SHA-256: $JUNIMOGATE_MONOGAME_OPENAL_ARM64_SHA256

The recorded Android binary was built for API 23 with Android NDK r27d. Set
\`ANDROID_NDK_HOME\` to that NDK, then run \`./build.sh\` from the archive root.
The provider build files show the exact CMake options and output layout. A
modified library can be placed at the matching MonoGame dependency path before
rebuilding JunimoGate.
EOF

archive_name="openal-soft-$JUNIMOGATE_OPENAL_PACKAGE_VERSION-corresponding-source.tar.gz"
archive_path="$output_dir/$archive_name"
tar \
  --sort=name \
  --mtime='UTC 2026-02-07 13:25:00' \
  --owner=0 --group=0 --numeric-owner \
  -cf - -C "$working_dir" "$(basename "$bundle_root")" \
  | gzip -n > "$archive_path.partial"
mv "$archive_path.partial" "$archive_path"
(
  cd "$output_dir"
  sha256sum "$archive_name" > "$archive_name.sha256"
)

printf 'OpenAL corresponding source: %s\n' "$archive_path"
printf 'Checksum: %s.sha256\n' "$archive_path"
