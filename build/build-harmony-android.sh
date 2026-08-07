#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=android-env.sh
source "$root/build/android-env.sh"
# shellcheck source=harmony-android-versions.sh
source "$root/build/harmony-android-versions.sh"

force=false
if [[ "${1:-}" == "--force" ]]; then
  force=true
elif [[ $# -ne 0 ]]; then
  echo "Usage: $0 [--force]" >&2
  exit 2
fi

proxy_url="${JUNIMOGATE_PROXY_URL:-}"
if [[ -n "$proxy_url" ]]; then
  export HTTP_PROXY="$proxy_url" HTTPS_PROXY="$proxy_url" ALL_PROXY="$proxy_url"
  export http_proxy="$proxy_url" https_proxy="$proxy_url" all_proxy="$proxy_url"
  curl --fail --silent --show-error --head --max-time 15 \
    --proxy "$proxy_url" https://api.nuget.org/v3/index.json >/dev/null
fi

cache="$JUNIMOGATE_TOOLCHAINS_DIR/source-cache/harmony-android"
build_root="$JUNIMOGATE_TOOLCHAINS_DIR/source-build/harmony-android"
feed="$root/artifacts/nuget"
patch="$root/patches/harmony-android/harmony-2.4.2-android.patch"
package="$feed/Lib.Harmony.$HARMONY_ANDROID_PACKAGE_VERSION.nupkg"
provenance="$feed/Lib.Harmony.$HARMONY_ANDROID_PACKAGE_VERSION.provenance.json"
mkdir -p "$cache" "$feed"

patch_sha256="$(sha256sum "$patch" | awk '{print $1}')"
if [[ "$patch_sha256" != "$HARMONY_ANDROID_PATCH_SHA256" ]]; then
  printf 'Tracked Harmony patch SHA-256 mismatch.\nexpected: %s\nactual:   %s\n' \
    "$HARMONY_ANDROID_PATCH_SHA256" "$patch_sha256" >&2
  exit 1
fi

validate_package() {
  python3 - "$package" "$provenance" \
    "$patch_sha256" \
    "$HARMONY_COMMIT" "$HARMONY_ARCHIVE_SHA256" \
    "$MONOMOD_COMMIT" "$MONOMOD_ARCHIVE_SHA256" \
    "$ICED_COMMIT" "$ICED_ARCHIVE_SHA256" <<'PY'
import hashlib
import json
import pathlib
import sys
import zipfile

package = pathlib.Path(sys.argv[1])
provenance = pathlib.Path(sys.argv[2])
expected_inputs = {
    "patchSha256": sys.argv[3],
    "harmonyCommit": sys.argv[4],
    "harmonyArchiveSha256": sys.argv[5],
    "monoModCommit": sys.argv[6],
    "monoModArchiveSha256": sys.argv[7],
    "icedCommit": sys.argv[8],
    "icedArchiveSha256": sys.argv[9],
}

def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()

try:
    package_bytes = package.read_bytes()
    value = json.loads(provenance.read_text(encoding="utf-8"))
    with zipfile.ZipFile(package) as archive:
        names = set(archive.namelist())
        assembly = archive.read("lib/net9.0/0Harmony.dll")
except Exception as error:
    print(f"Patched Harmony validation failed: {error}", file=sys.stderr)
    raise SystemExit(1)

actual_package_sha = digest(package_bytes)
actual_assembly_sha = digest(assembly)
if any(name.startswith("lib/netstandard") for name in names):
    print("Patched Harmony package unexpectedly contains netstandard assets", file=sys.stderr)
    raise SystemExit(1)
if value.get("inputs") != expected_inputs:
    print("Patched Harmony provenance inputs do not match pinned inputs", file=sys.stderr)
    raise SystemExit(1)
recorded = value.get("package", {})
if recorded.get("sha256") != actual_package_sha or recorded.get("assemblySha256") != actual_assembly_sha:
    print("Patched Harmony provenance does not match package bytes", file=sys.stderr)
    raise SystemExit(1)
PY
}

if [[ "$force" != true && -f "$package" && -f "$provenance" ]] && validate_package; then
  printf 'Patched Harmony package is current: %s\n' "$package"
  exit 0
fi

download_verified() {
  local url="$1" expected="$2" destination="$3"
  if [[ ! -f "$destination" ]]; then
    printf 'Downloading %s\n' "$url"
    curl --fail --location --retry 4 --retry-delay 2 --continue-at - \
      --output "$destination.part" "$url"
    mv "$destination.part" "$destination"
  fi
  local actual
  actual="$(sha256sum "$destination" | awk '{print $1}')"
  if [[ "$actual" != "$expected" ]]; then
    printf 'SHA-256 mismatch for %s\nexpected: %s\nactual:   %s\n' \
      "$destination" "$expected" "$actual" >&2
    exit 1
  fi
}

harmony_archive="$cache/harmony-$HARMONY_COMMIT.tar.gz"
monomod_archive="$cache/monomod-$MONOMOD_COMMIT.tar.gz"
iced_archive="$cache/iced-$ICED_COMMIT.tar.gz"
download_verified "$HARMONY_ARCHIVE_URL" "$HARMONY_ARCHIVE_SHA256" "$harmony_archive"
download_verified "$MONOMOD_ARCHIVE_URL" "$MONOMOD_ARCHIVE_SHA256" "$monomod_archive"
download_verified "$ICED_ARCHIVE_URL" "$ICED_ARCHIVE_SHA256" "$iced_archive"

python3 - "$build_root" <<'PY'
import pathlib,shutil,sys
path=pathlib.Path(sys.argv[1])
if path.exists():
    shutil.rmtree(path)
path.mkdir(parents=True)
PY

extract_single_root() {
  local archive="$1" destination="$2" label="$3"
  local temporary="$build_root/extract-$label"
  mkdir "$temporary"
  tar -xzf "$archive" -C "$temporary"
  mapfile -t roots < <(find "$temporary" -mindepth 1 -maxdepth 1 -type d -print)
  if ((${#roots[@]} != 1)); then
    printf '%s archive must contain exactly one root directory.\n' "$label" >&2
    exit 1
  fi
  mkdir -p "$(dirname "$destination")"
  mv "${roots[0]}" "$destination"
  rmdir "$temporary"
}

source_root="$build_root/source"
extract_single_root "$harmony_archive" "$source_root" harmony
python3 - "$source_root/LocalMonoMod" <<'PY'
import pathlib,shutil,sys
path=pathlib.Path(sys.argv[1])
if path.exists():
    shutil.rmtree(path)
PY
extract_single_root "$monomod_archive" "$source_root/LocalMonoMod" monomod
python3 - "$source_root/LocalMonoMod/external/iced" <<'PY'
import pathlib,shutil,sys
path=pathlib.Path(sys.argv[1])
if path.exists():
    shutil.rmtree(path)
PY
extract_single_root "$iced_archive" "$source_root/LocalMonoMod/external/iced" iced

(
  cd "$source_root"
  # The extracted source lives under JunimoGate's ignored .toolchains directory.
  # Without a nested repository, git apply discovers the parent repository,
  # ignores every extracted path, and can report success after changing 0 files.
  git init --quiet
  git apply --check "$patch"
  git apply "$patch"
  [[ -n "$(git status --short --untracked-files=all)" ]] || {
    echo "Harmony Android patch reported success but changed no source files." >&2
    exit 1
  }
)

expected_prerelease="-${HARMONY_ANDROID_PACKAGE_VERSION#2.4.2-}"
grep -Fq "<HarmonyPrerelease>$expected_prerelease</HarmonyPrerelease>" "$source_root/Directory.Build.props"
grep -Fq 'OSKind.Android => CreateAndroidSystem()' "$source_root/LocalMonoMod/src/MonoMod.Core/Platforms/PlatformTriple.cs"
grep -Fq 'AndroidPageSize = 0x0027' "$source_root/LocalMonoMod/src/MonoMod.Core/Interop/Unix.cs"
grep -Fq 'MonoCompileMethod(handle.Value)' "$source_root/LocalMonoMod/src/MonoMod.Core/Platforms/Runtimes/MonoRuntime.cs"
grep -Fq 'Unix.JunimoGateClearCache(start, end)' "$source_root/LocalMonoMod/src/MonoMod.Core/Platforms/Systems/LinuxSystem.cs"
[[ -f "$source_root/LocalMonoMod/src/MonoMod.Core/Interop/AndroidNativeLibraryResolver.cs" ]]

rm -f "$package" "$provenance"
printf 'Building patched Harmony %s from pinned sources...\n' "$HARMONY_ANDROID_PACKAGE_VERSION"
pushd "$source_root" >/dev/null
"$DOTNET_ROOT/dotnet" restore Lib.Harmony/Lib.Harmony.csproj \
  --disable-parallel \
  --source https://api.nuget.org/v3/index.json
"$DOTNET_ROOT/dotnet" build Lib.Harmony/Lib.Harmony.csproj \
  --configuration Release \
  --framework net9.0 \
  --no-restore \
  --disable-build-servers \
  --maxcpucount:1 \
  --property:ContinuousIntegrationBuild=true
"$DOTNET_ROOT/dotnet" pack Lib.Harmony/Lib.Harmony.csproj \
  --configuration Release \
  --no-build \
  --output "$feed" \
  --property:ContinuousIntegrationBuild=true
popd >/dev/null

[[ -f "$package" ]] || { echo "Expected package was not produced: $package" >&2; exit 1; }
python3 - "$package" "$provenance" "$patch_sha256" \
  "$HARMONY_COMMIT" "$HARMONY_ARCHIVE_SHA256" \
  "$MONOMOD_COMMIT" "$MONOMOD_ARCHIVE_SHA256" \
  "$ICED_COMMIT" "$ICED_ARCHIVE_SHA256" <<'PY'
import datetime as dt
import hashlib
import json
import pathlib
import sys
import tempfile
import zipfile

package=pathlib.Path(sys.argv[1])
provenance=pathlib.Path(sys.argv[2])
patch_sha=sys.argv[3]

def digest(data):
    return hashlib.sha256(data).hexdigest()

package_bytes=package.read_bytes()
with zipfile.ZipFile(package) as archive:
    names=set(archive.namelist())
    required="lib/net9.0/0Harmony.dll"
    if required not in names:
        raise SystemExit(f"package is missing {required}")
    if any(name.startswith("lib/netstandard") for name in names):
        raise SystemExit("net9-only package unexpectedly contains netstandard assets")
    assembly=archive.read(required)

value={
    "schemaVersion": 1,
    "generatedAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
    "package": {
        "path": str(package),
        "sha256": digest(package_bytes),
        "size": len(package_bytes),
        "assemblyPath": required,
        "assemblySha256": digest(assembly),
        "assemblySize": len(assembly),
    },
    "inputs": {
        "harmonyCommit": sys.argv[4],
        "harmonyArchiveSha256": sys.argv[5],
        "monoModCommit": sys.argv[6],
        "monoModArchiveSha256": sys.argv[7],
        "icedCommit": sys.argv[8],
        "icedArchiveSha256": sys.argv[9],
        "patchSha256": patch_sha,
    },
}
encoded=json.dumps(value,indent=2)+"\n"
provenance.parent.mkdir(parents=True,exist_ok=True)
with tempfile.NamedTemporaryFile("w",encoding="utf-8",dir=provenance.parent,delete=False) as stream:
    temporary=pathlib.Path(stream.name)
    stream.write(encoded)
temporary.replace(provenance)
print(encoded,end="")
PY

validate_package
printf 'Verified patched Harmony package: %s\n' "$package"
