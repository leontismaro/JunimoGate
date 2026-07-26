#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=android-env.sh
source "$root/build/android-env.sh"
# shellcheck source=monogame-android-versions.sh
source "$root/build/monogame-android-versions.sh"

cache_root="$root/.toolchains/source-cache/monogame-android"
source_root="$root/.toolchains/monogame-android-source"
package_output="$root/artifacts/nuget"
provenance_output="$root/artifacts/build-environment/monogame-android-build.json"
proxy_url="${JUNIMOGATE_PROXY_URL:-}"

mkdir -p "$cache_root" "$source_root" "$package_output" "$(dirname "$provenance_output")"

monogame_license="$root/licenses/MonoGame-f5d8bf.txt"
openal_license="$root/licenses/OpenAL-Soft-1.16.0-COPYING.txt"
stb_notice="$root/licenses/StbSharp-PUBLIC-DOMAIN.txt"

sha256_file() {
  sha256sum "$1" | awk '{print $1}'
}

require_sha256() {
  local path="$1"
  local expected="$2"
  local label="$3"
  local actual
  actual="$(sha256_file "$path")"
  if [[ "$actual" != "$expected" ]]; then
    printf '%s SHA-256 mismatch: expected %s, got %s\n' "$label" "$expected" "$actual" >&2
    return 1
  fi
}

download_verified() {
  local url="$1"
  local destination="$2"
  local expected_sha256="$3"
  local label="$4"

  if [[ -f "$destination" ]] && require_sha256 "$destination" "$expected_sha256" "$label"; then
    printf '%s archive is current: %s\n' "$label" "$destination"
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

  printf 'Downloading %s...\n' "$label"
  curl "${curl_args[@]}" "$url"
  require_sha256 "$destination.partial" "$expected_sha256" "$label"
  mv "$destination.partial" "$destination"
}

extract_archive() {
  local archive="$1"
  local destination="$2"
  mkdir -p "$destination"
  find "$destination" -mindepth 1 -delete
  tar -xzf "$archive" -C "$destination" --strip-components=1
}

require_sha256 "$monogame_license" "$JUNIMOGATE_MONOGAME_LICENSE_SHA256" "Tracked MonoGame license"
require_sha256 "$openal_license" "$JUNIMOGATE_MONOGAME_OPENAL_LICENSE_SHA256" "Tracked OpenAL license"
require_sha256 "$stb_notice" "$JUNIMOGATE_MONOGAME_STB_NOTICE_SHA256" "Tracked Stb public-domain notice"

monogame_archive="$cache_root/monogame-$JUNIMOGATE_MONOGAME_COMMIT.tar.gz"
dependencies_archive="$cache_root/monogame-dependencies-$JUNIMOGATE_MONOGAME_DEPENDENCIES_COMMIT.tar.gz"
stb_image_archive="$cache_root/stb-image-$JUNIMOGATE_STB_IMAGE_COMMIT.tar.gz"
stb_write_archive="$cache_root/stb-image-write-$JUNIMOGATE_STB_IMAGE_WRITE_COMMIT.tar.gz"

download_verified \
  "https://codeload.github.com/MonoGame/MonoGame/tar.gz/$JUNIMOGATE_MONOGAME_COMMIT" \
  "$monogame_archive" \
  "$JUNIMOGATE_MONOGAME_ARCHIVE_SHA256" \
  "MonoGame"
download_verified \
  "https://codeload.github.com/ConcernedApe/MonoGame.Dependencies/tar.gz/$JUNIMOGATE_MONOGAME_DEPENDENCIES_COMMIT" \
  "$dependencies_archive" \
  "$JUNIMOGATE_MONOGAME_DEPENDENCIES_ARCHIVE_SHA256" \
  "MonoGame.Dependencies"
download_verified \
  "https://codeload.github.com/StbSharp/StbImageSharp/tar.gz/$JUNIMOGATE_STB_IMAGE_COMMIT" \
  "$stb_image_archive" \
  "$JUNIMOGATE_STB_IMAGE_ARCHIVE_SHA256" \
  "StbImageSharp"
download_verified \
  "https://codeload.github.com/StbSharp/StbImageWriteSharp/tar.gz/$JUNIMOGATE_STB_IMAGE_WRITE_COMMIT" \
  "$stb_write_archive" \
  "$JUNIMOGATE_STB_IMAGE_WRITE_ARCHIVE_SHA256" \
  "StbImageWriteSharp"

printf 'Preparing clean public MonoGame source tree...\n'
extract_archive "$monogame_archive" "$source_root"
extract_archive "$dependencies_archive" "$source_root/ThirdParty/Dependencies"
extract_archive "$stb_image_archive" "$source_root/ThirdParty/StbImageSharp"
extract_archive "$stb_write_archive" "$source_root/ThirdParty/StbImageWriteSharp"

project="$source_root/MonoGame.Framework/MonoGame.Framework.Android.csproj"
[[ -f "$project" ]] || { echo "MonoGame Android project is missing after extraction." >&2; exit 3; }
[[ -f "$source_root/LICENSE.txt" ]] || { echo "MonoGame Ms-PL license is missing after extraction." >&2; exit 3; }
require_sha256 "$source_root/LICENSE.txt" "$JUNIMOGATE_MONOGAME_LICENSE_SHA256" "MonoGame source license"
[[ -f "$source_root/ThirdParty/Dependencies/openal-soft/libs/arm64-v8a/libopenal32.so" ]] || {
  echo "Pinned ARM64 OpenAL library is missing after dependency extraction." >&2
  exit 3
}
require_sha256 \
  "$source_root/ThirdParty/Dependencies/openal-soft/libs/arm64-v8a/libopenal32.so" \
  "$JUNIMOGATE_MONOGAME_OPENAL_ARM64_SHA256" \
  "ARM64 OpenAL"

grep -Fq '<TargetFramework>net9.0-android35</TargetFramework>' "$project" || {
  echo "Pinned MonoGame source no longer declares net9.0-android35." >&2
  exit 3
}
grep -Fq 'public event EventHandler<TextInputEventArgs> TextInput' \
  "$source_root/MonoGame.Framework/GameWindow.cs" || {
  echo "Pinned MonoGame source is missing the tested Android TextInput API." >&2
  exit 3
}
grep -R -Fq 'public static float TextureTuckAmount' "$source_root/MonoGame.Framework" || {
  echo "Pinned MonoGame source is missing the tested SpriteBatch compatibility API." >&2
  exit 3
}

escaped_nowarn="${JUNIMOGATE_MONOGAME_NOWARN//;/%3B}"
escaped_msbuild_warnings_as_messages="${JUNIMOGATE_MONOGAME_MSBUILD_WARNINGS_AS_MESSAGES//;/%3B}"

common_properties=(
  "-p:AssemblyVersion=$JUNIMOGATE_MONOGAME_ASSEMBLY_VERSION"
  "-p:FileVersion=$JUNIMOGATE_MONOGAME_ASSEMBLY_VERSION"
  "-p:Version=1.0.0"
  "-p:PackageVersion=$JUNIMOGATE_MONOGAME_PACKAGE_VERSION"
  "-p:PackageId=$JUNIMOGATE_MONOGAME_PACKAGE_ID"
  "-p:PackageLicenseExpression=MS-PL"
  "-p:PackageProjectUrl=$JUNIMOGATE_MONOGAME_REPOSITORY"
  "-p:RepositoryUrl=$JUNIMOGATE_MONOGAME_REPOSITORY"
  "-p:RepositoryType=git"
  "-p:RepositoryCommit=$JUNIMOGATE_MONOGAME_COMMIT"
  "-p:SourceRevisionId=$JUNIMOGATE_MONOGAME_COMMIT"
  "-p:Deterministic=true"
  "-p:ContinuousIntegrationBuild=true"
  "-p:DebugType=none"
  "-p:DebugSymbols=false"
  "-p:EmbedUntrackedSources=false"
  "-p:DeterministicSourcePaths=true"
  "-p:PathMap=$source_root=/_/monogame"
  "-p:UseSharedCompilation=false"
  "-p:TreatWarningsAsErrors=true"
  "-p:NoWarn=$escaped_nowarn"
  "-p:MSBuildWarningsAsMessages=$escaped_msbuild_warnings_as_messages"
)

export DOTNET_CLI_USE_MSBUILD_SERVER=0
export MSBUILDDISABLENODEREUSE=1

printf 'Restoring public MonoGame Android project...\n'
"$DOTNET_ROOT/dotnet" restore "$project" \
  --configfile "$root/NuGet.Config" \
  "${common_properties[@]}"

printf 'Building deterministic public MonoGame Android provider...\n'
"$DOTNET_ROOT/dotnet" build "$project" \
  --configuration Release \
  --no-restore \
  "${common_properties[@]}"

build_output="$source_root/Artifacts/MonoGame.Framework/Android/Release"
dll="$build_output/MonoGame.Framework.dll"
xml="$build_output/MonoGame.Framework.xml"
aar="$build_output/MonoGame.Framework.aar"
targets="$source_root/MonoGame.Framework/MonoGame.Framework.Android.targets"

python3 - "$aar" <<'PY'
import pathlib
import zipfile

path = pathlib.Path(__import__("sys").argv[1])
temporary = path.with_name(f".{path.name}.normalized")
with zipfile.ZipFile(path) as source, zipfile.ZipFile(temporary, "w", allowZip64=True) as output:
    for entry in sorted(source.infolist(), key=lambda value: value.filename):
        normalized = zipfile.ZipInfo(entry.filename, (1980, 1, 1, 0, 0, 0))
        normalized.compress_type = zipfile.ZIP_STORED
        normalized.create_system = 3
        normalized.external_attr = entry.external_attr
        normalized.flag_bits = 0
        output.writestr(normalized, source.read(entry.filename))
temporary.replace(path)
PY

[[ -f "$dll" && -f "$xml" && -f "$aar" && -f "$targets" ]] || {
  echo "MonoGame build did not produce the expected DLL/XML/AAR/targets files." >&2
  exit 4
}
[[ "$(stat -c '%s' "$dll")" == "$JUNIMOGATE_MONOGAME_DLL_SIZE" ]] || {
  echo "MonoGame managed provider size does not match the pinned deterministic output." >&2
  exit 4
}
[[ "$(stat -c '%s' "$aar")" == "$JUNIMOGATE_MONOGAME_AAR_SIZE" ]] || {
  echo "MonoGame AAR size does not match the pinned deterministic output." >&2
  exit 4
}
require_sha256 "$dll" "$JUNIMOGATE_MONOGAME_DLL_SHA256" "MonoGame managed provider"
require_sha256 "$xml" "$JUNIMOGATE_MONOGAME_XML_SHA256" "MonoGame XML documentation"
require_sha256 "$aar" "$JUNIMOGATE_MONOGAME_AAR_SHA256" "MonoGame Android AAR"
require_sha256 "$targets" "$JUNIMOGATE_MONOGAME_TARGETS_SHA256" "MonoGame package targets"

pack_root="$source_root/package-output"
mkdir -p "$pack_root"
find "$pack_root" -mindepth 1 -delete
printf 'Packing deterministic public MonoGame provider...\n'
"$DOTNET_ROOT/dotnet" pack "$project" \
  --configuration Release \
  --no-build \
  --no-restore \
  --output "$pack_root" \
  "${common_properties[@]}"

package_name="$JUNIMOGATE_MONOGAME_PACKAGE_ID.$JUNIMOGATE_MONOGAME_PACKAGE_VERSION.nupkg"
built_package="$pack_root/$package_name"
[[ -f "$built_package" ]] || { echo "Expected MonoGame NuGet package was not produced." >&2; exit 5; }

python3 - "$built_package" "$monogame_license" "$openal_license" "$stb_notice" <<'PY'
import pathlib
import re
import sys
import zipfile

package = pathlib.Path(sys.argv[1])
license_entries = {
    "licenses/MonoGame-f5d8bf.txt": pathlib.Path(sys.argv[2]).read_bytes(),
    "licenses/OpenAL-Soft-1.16.0-COPYING.txt": pathlib.Path(sys.argv[3]).read_bytes(),
    "licenses/StbSharp-PUBLIC-DOMAIN.txt": pathlib.Path(sys.argv[4]).read_bytes(),
}
temporary = package.with_name(f".{package.name}.normalized")
fixed_core = "package/services/metadata/core-properties/junimogate-monogame.psmdcp"
with zipfile.ZipFile(package) as source:
    entries = {
        info.filename: source.read(info)
        for info in source.infolist()
        if info.filename != "_rels/.rels"
        and not (
            info.filename.startswith("package/services/metadata/core-properties/")
            and info.filename.endswith(".psmdcp")
        )
    }
    core_entries = [
        info for info in source.infolist()
        if info.filename.startswith("package/services/metadata/core-properties/")
        and info.filename.endswith(".psmdcp")
    ]
    if len(core_entries) != 1:
        raise SystemExit("MonoGame package must contain exactly one core-properties document")
    core = source.read(core_entries[0])
    relationships = source.read("_rels/.rels").decode("utf-8")

relationships = re.sub(
    r'<Relationship Type="http://schemas.microsoft.com/packaging/2010/07/manifest" Target="/([^\"]+)" Id="R[^\"]+" />',
    r'<Relationship Type="http://schemas.microsoft.com/packaging/2010/07/manifest" Target="/\1" Id="Rmanifest" />',
    relationships,
)
relationships = re.sub(
    r'<Relationship Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="/package/services/metadata/core-properties/[^\"]+\.psmdcp" Id="R[^\"]+" />',
    f'<Relationship Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="/{fixed_core}" Id="Rcore" />',
    relationships,
)
if 'Id="Rmanifest"' not in relationships or 'Id="Rcore"' not in relationships:
    raise SystemExit("MonoGame package relationships could not be normalized")
entries["_rels/.rels"] = relationships.encode("utf-8")
entries[fixed_core] = core
entries.update(license_entries)

with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_STORED, allowZip64=True) as output:
    for name in sorted(entries):
        info = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_STORED
        info.create_system = 3
        info.external_attr = 0o100644 << 16
        output.writestr(info, entries[name])
temporary.replace(package)
PY

[[ "$(stat -c '%s' "$built_package")" == "$JUNIMOGATE_MONOGAME_PACKAGE_SIZE" ]] || {
  echo "MonoGame NuGet package size does not match the pinned deterministic output." >&2
  exit 5
}
require_sha256 "$built_package" "$JUNIMOGATE_MONOGAME_PACKAGE_SHA256" "MonoGame NuGet package"

python3 - "$built_package" \
  "$JUNIMOGATE_MONOGAME_PACKAGE_ID" \
  "$JUNIMOGATE_MONOGAME_PACKAGE_VERSION" \
  "$JUNIMOGATE_MONOGAME_DLL_SIZE" \
  "$JUNIMOGATE_MONOGAME_DLL_SHA256" \
  "$JUNIMOGATE_MONOGAME_XML_SHA256" \
  "$JUNIMOGATE_MONOGAME_AAR_SIZE" \
  "$JUNIMOGATE_MONOGAME_AAR_SHA256" \
  "$JUNIMOGATE_MONOGAME_TARGETS_SHA256" \
  "$JUNIMOGATE_MONOGAME_LICENSE_SHA256" \
  "$JUNIMOGATE_MONOGAME_OPENAL_LICENSE_SHA256" \
  "$JUNIMOGATE_MONOGAME_STB_NOTICE_SHA256" <<'PY'
import hashlib
import io
import sys
import zipfile

(
    package_path,
    package_id,
    package_version,
    expected_dll_size,
    expected_dll_hash,
    expected_xml_hash,
    expected_aar_size,
    expected_aar_hash,
    expected_targets_hash,
    expected_monogame_license_hash,
    expected_openal_license_hash,
    expected_stb_notice_hash,
) = sys.argv[1:]
expected = {
    f"lib/net9.0-android35.0/MonoGame.Framework.dll": (int(expected_dll_size), expected_dll_hash),
    f"lib/net9.0-android35.0/MonoGame.Framework.xml": (None, expected_xml_hash),
    f"lib/net9.0-android35.0/MonoGame.Framework.aar": (int(expected_aar_size), expected_aar_hash),
    f"build/MonoGame.Framework.Android.targets": (None, expected_targets_hash),
    "licenses/MonoGame-f5d8bf.txt": (None, expected_monogame_license_hash),
    "licenses/OpenAL-Soft-1.16.0-COPYING.txt": (None, expected_openal_license_hash),
    "licenses/StbSharp-PUBLIC-DOMAIN.txt": (None, expected_stb_notice_hash),
}
with zipfile.ZipFile(package_path) as archive:
    names = set(archive.namelist())
    for name, (size, digest) in expected.items():
        if name not in names:
            raise SystemExit(f"MonoGame package is missing {name}")
        data = archive.read(name)
        if size is not None and len(data) != size:
            raise SystemExit(f"MonoGame package entry size mismatch: {name}")
        if hashlib.sha256(data).hexdigest() != digest:
            raise SystemExit(f"MonoGame package entry hash mismatch: {name}")
    nuspec = archive.read(f"{package_id}.nuspec").decode("utf-8")
    for token in (
        f"<id>{package_id}</id>",
        f"<version>{package_version}</version>",
        '<license type="expression">MS-PL</license>',
        "f5d8bfbb4ac9847540b3c898e6237104ee98c149",
    ):
        if token not in nuspec:
            raise SystemExit(f"MonoGame package nuspec is missing {token}")
PY

installed_package="$package_output/$package_name"
install -m 0644 "$built_package" "$installed_package"
require_sha256 "$installed_package" "$JUNIMOGATE_MONOGAME_PACKAGE_SHA256" "Installed MonoGame NuGet package"

android_workload_version="$JUNIMOGATE_MONOGAME_ANDROID_SDK_PACK_VERSION"
if [[ ! -d "$DOTNET_ROOT/packs/Microsoft.Android.Sdk.Linux/$android_workload_version" ]]; then
  echo "Pinned Microsoft.Android.Sdk.Linux pack $android_workload_version is not installed." >&2
  exit 6
fi

python3 - "$provenance_output" "$installed_package" <<PY
import datetime as dt
import hashlib
import json
import pathlib
import sys

output = pathlib.Path(sys.argv[1])
package = pathlib.Path(sys.argv[2])
report = {
    "schemaVersion": 1,
    "generatedAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
    "source": {
        "repository": "$JUNIMOGATE_MONOGAME_REPOSITORY",
        "commit": "$JUNIMOGATE_MONOGAME_COMMIT",
        "archiveSha256": "$JUNIMOGATE_MONOGAME_ARCHIVE_SHA256",
        "license": "MS-PL",
    },
    "dependencies": [
        {"repository": "$JUNIMOGATE_MONOGAME_DEPENDENCIES_REPOSITORY", "commit": "$JUNIMOGATE_MONOGAME_DEPENDENCIES_COMMIT", "archiveSha256": "$JUNIMOGATE_MONOGAME_DEPENDENCIES_ARCHIVE_SHA256"},
        {"repository": "$JUNIMOGATE_STB_IMAGE_REPOSITORY", "commit": "$JUNIMOGATE_STB_IMAGE_COMMIT", "archiveSha256": "$JUNIMOGATE_STB_IMAGE_ARCHIVE_SHA256"},
        {"repository": "$JUNIMOGATE_STB_IMAGE_WRITE_REPOSITORY", "commit": "$JUNIMOGATE_STB_IMAGE_WRITE_COMMIT", "archiveSha256": "$JUNIMOGATE_STB_IMAGE_WRITE_ARCHIVE_SHA256"},
    ],
    "licenses": [
        {"path": "licenses/MonoGame-f5d8bf.txt", "sha256": "$JUNIMOGATE_MONOGAME_LICENSE_SHA256", "classification": "Ms-PL with upstream Mono.Xna MIT notice"},
        {"path": "licenses/OpenAL-Soft-1.16.0-COPYING.txt", "sha256": "$JUNIMOGATE_MONOGAME_OPENAL_LICENSE_SHA256", "classification": "GNU Library General Public License v2"},
        {"path": "licenses/StbSharp-PUBLIC-DOMAIN.txt", "sha256": "$JUNIMOGATE_MONOGAME_STB_NOTICE_SHA256", "classification": "Public Domain notices"},
    ],
    "build": {
        "dotnetSdk": "$DOTNET_SDK_VERSION",
        "androidWorkload": "$android_workload_version",
        "targetFramework": "net9.0-android35.0",
        "assemblyVersion": "$JUNIMOGATE_MONOGAME_ASSEMBLY_VERSION",
        "deterministic": True,
        "continuousIntegrationBuild": True,
        "debugType": "none",
        "pathMap": "/_/monogame",
        "suppressedWarningCodes": "$JUNIMOGATE_MONOGAME_NOWARN".split(";"),
        "msbuildWarningsAsMessages": "$JUNIMOGATE_MONOGAME_MSBUILD_WARNINGS_AS_MESSAGES".split(";"),
    },
    "package": {
        "id": "$JUNIMOGATE_MONOGAME_PACKAGE_ID",
        "version": "$JUNIMOGATE_MONOGAME_PACKAGE_VERSION",
        "path": package.name,
        "size": package.stat().st_size,
        "sha256": hashlib.sha256(package.read_bytes()).hexdigest(),
        "managedProviderSha256": "$JUNIMOGATE_MONOGAME_DLL_SHA256",
        "arm64OpenAlSha256": "$JUNIMOGATE_MONOGAME_OPENAL_ARM64_SHA256",
    },
}
output.parent.mkdir(parents=True, exist_ok=True)
temporary = output.with_name(f".{output.name}.tmp")
temporary.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
temporary.replace(output)
PY

"$DOTNET_ROOT/dotnet" build-server shutdown >/dev/null 2>&1 || true
printf 'Deterministic public MonoGame provider is current: %s\n' "$installed_package"
printf 'Build provenance: %s\n' "$provenance_output"
