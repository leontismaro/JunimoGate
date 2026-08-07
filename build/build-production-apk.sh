#!/usr/bin/env bash
set -euo pipefail
set +x

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=android-env.sh
source "$root/build/android-env.sh"

release_keystore="${JUNIMOGATE_RELEASE_KEYSTORE:-}"
release_alias="junimogate-release"
expected_certificate_sha256="fa26f2f183f4be61620423bf5585814a63af05caedf6e33dd77425a428c81036"
unsigned_apk="$root/src/JunimoGate.App/bin/Release/net9.0-android35.0/android-arm64/org.junimogate.app.apk"
output_dir="$root/artifacts/release"
apksigner="$ANDROID_SDK_ROOT/build-tools/$ANDROID_BUILD_TOOLS_VERSION/apksigner"
aapt="$ANDROID_SDK_ROOT/build-tools/$ANDROID_BUILD_TOOLS_VERSION/aapt"
sign_only=false

display_path() {
  local path="$1"
  if [[ "$path" == "$root/"* ]]; then
    printf '%s' "${path#"$root/"}"
  elif [[ "$path" == "$HOME/"* ]]; then
    printf '$HOME/%s' "${path#"$HOME/"}"
  else
    printf '%s' "$path"
  fi
}

usage() {
  cat <<'EOF'
Usage: ./build/build-production-apk.sh [--sign-only]

Build and sign the ARM64 Release APK with the pinned JunimoGate release
certificate. --sign-only reuses the current unsigned Release APK.

Set JUNIMOGATE_RELEASE_KEYSTORE to the protected PKCS12 keystore path before
running this command. The script has no built-in keystore path. It prompts for
the password unless JUNIMOGATE_RELEASE_KEYSTORE_PASSWORD is already set by a
protected CI secret.
EOF
}

case "${1:-}" in
  "") ;;
  --sign-only) sign_only=true ;;
  -h|--help) usage; exit 0 ;;
  *) usage >&2; exit 2 ;;
esac
if (($# > 1)); then
  usage >&2
  exit 2
fi

[[ -n "$release_keystore" ]] || {
  echo "JUNIMOGATE_RELEASE_KEYSTORE must point to the release PKCS12 keystore." >&2
  exit 3
}
[[ -f "$release_keystore" ]] || {
  echo "The configured release keystore was not found." >&2
  exit 3
}
[[ -x "$apksigner" ]] || { echo "Android apksigner is unavailable." >&2; exit 3; }
[[ -x "$aapt" ]] || { echo "Android aapt is unavailable." >&2; exit 3; }

keystore_mode="$(stat -c '%a' "$release_keystore")"
if (( (8#$keystore_mode & 077) != 0 )); then
  printf 'Release keystore permissions must deny group/other access; found mode %s.\n' \
    "$keystore_mode" >&2
  exit 3
fi

if [[ "$sign_only" == false ]]; then
  printf '[1/4] Building the unsigned ARM64 Release APK...\n'
  "$root/build/build-android.sh" Release app
else
  printf '[1/4] Reusing the current unsigned ARM64 Release APK...\n'
fi
[[ -f "$unsigned_apk" ]] || {
  printf 'Unsigned Release APK not found: %s\n' "$(display_path "$unsigned_apk")" >&2
  printf 'Run without --sign-only to build it first.\n' >&2
  exit 4
}

if [[ -z "${JUNIMOGATE_RELEASE_KEYSTORE_PASSWORD:-}" ]]; then
  [[ -t 0 ]] || {
    echo "A terminal is required to read the release keystore password." >&2
    exit 5
  }
  IFS= read -r -s -p "JunimoGate release keystore password: " JUNIMOGATE_RELEASE_KEYSTORE_PASSWORD
  printf '\n'
fi
[[ -n "$JUNIMOGATE_RELEASE_KEYSTORE_PASSWORD" ]] || {
  echo "The release keystore password cannot be empty." >&2
  exit 5
}
export -n JUNIMOGATE_RELEASE_KEYSTORE_PASSWORD 2>/dev/null || true

certificate_file="$(mktemp "${TMPDIR:-/tmp}/junimogate-release-certificate.XXXXXX")"
signed_temporary=""
cleanup() {
  unset JUNIMOGATE_RELEASE_KEYSTORE_PASSWORD
  [[ -z "$certificate_file" ]] || rm -f -- "$certificate_file"
  [[ -z "$signed_temporary" ]] || rm -f -- "$signed_temporary" "$signed_temporary.idsig"
}
trap cleanup EXIT

printf '[2/4] Verifying the pinned release certificate...\n'
JUNIMOGATE_RELEASE_KEYSTORE_PASSWORD="$JUNIMOGATE_RELEASE_KEYSTORE_PASSWORD" \
"$JAVA_HOME/bin/keytool" -exportcert \
  -keystore "$release_keystore" \
  -storetype PKCS12 \
  -alias "$release_alias" \
  -storepass:env JUNIMOGATE_RELEASE_KEYSTORE_PASSWORD \
  > "$certificate_file"
actual_certificate_sha256="$(sha256sum "$certificate_file" | awk '{print $1}')"
if [[ "$actual_certificate_sha256" != "$expected_certificate_sha256" ]]; then
  printf 'Release certificate mismatch: expected %s, got %s.\n' \
    "$expected_certificate_sha256" "$actual_certificate_sha256" >&2
  exit 6
fi

package_badging="$($aapt dump badging "$unsigned_apk")"
version_name="$(sed -n "s/^package: .* versionName='\([^']*\)'.*/\1/p" <<<"$package_badging" | head -n 1)"
[[ "$version_name" =~ ^[0-9A-Za-z][0-9A-Za-z.+-]{0,63}$ ]] || {
  printf 'The APK version name is missing or unsafe for an artifact name: %s\n' "$version_name" >&2
  exit 7
}
if [[ "$version_name" == *-dev* ]]; then
  printf 'Production signing requires a release version; found %s.\n' "$version_name" >&2
  printf 'Update ApplicationDisplayVersion and ApplicationVersion before signing.\n' >&2
  exit 7
fi

mkdir -p "$output_dir"
signed_temporary="$(mktemp "$output_dir/.JunimoGate-$version_name-arm64.XXXXXX.apk")"
printf '[3/4] Signing JunimoGate %s...\n' "$version_name"
JUNIMOGATE_RELEASE_KEYSTORE_PASSWORD="$JUNIMOGATE_RELEASE_KEYSTORE_PASSWORD" \
"$apksigner" sign \
  --ks "$release_keystore" \
  --ks-type PKCS12 \
  --ks-key-alias "$release_alias" \
  --ks-pass env:JUNIMOGATE_RELEASE_KEYSTORE_PASSWORD \
  --key-pass env:JUNIMOGATE_RELEASE_KEYSTORE_PASSWORD \
  --v2-signing-enabled true \
  --v3-signing-enabled true \
  --v4-signing-enabled false \
  --out "$signed_temporary" \
  "$unsigned_apk"
unset JUNIMOGATE_RELEASE_KEYSTORE_PASSWORD

printf '[4/4] Verifying APK signatures and certificate...\n'
verification="$($apksigner verify --verbose --print-certs "$signed_temporary")"
grep -Fq 'Verified using v2 scheme (APK Signature Scheme v2): true' <<<"$verification" || {
  echo "The production APK is missing an APK Signature Scheme v2 signature." >&2
  exit 8
}
grep -Fq 'Verified using v3 scheme (APK Signature Scheme v3): true' <<<"$verification" || {
  echo "The production APK is missing an APK Signature Scheme v3 signature." >&2
  exit 8
}
apk_certificate_sha256="$(sed -n 's/^Signer #1 certificate SHA-256 digest: //p' <<<"$verification" | head -n 1)"
if [[ "$apk_certificate_sha256" != "$expected_certificate_sha256" ]]; then
  printf 'Signed APK certificate mismatch: expected %s, got %s.\n' \
    "$expected_certificate_sha256" "$apk_certificate_sha256" >&2
  exit 8
fi

final_apk="$output_dir/JunimoGate-$version_name-arm64.apk"
checksum_file="$final_apk.sha256"
mv -f -- "$signed_temporary" "$final_apk"
signed_temporary=""
(
  cd "$output_dir"
  sha256sum "$(basename "$final_apk")" > "$(basename "$checksum_file")"
)
chmod 0644 "$final_apk" "$checksum_file"

printf 'Production-signed APK: %s\n' "$(display_path "$final_apk")"
printf 'Certificate SHA-256: %s\n' "$expected_certificate_sha256"
printf 'Checksum: %s\n' "$(display_path "$checksum_file")"
printf 'No tag, GitHub Release, or upload was created.\n'
