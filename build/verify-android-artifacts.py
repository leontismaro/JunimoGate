#!/usr/bin/env python3
from __future__ import annotations

import datetime as dt
import hashlib
import json
import os
import pathlib
import re
import struct
import subprocess
import tempfile
import zipfile
from dataclasses import asdict, dataclass


@dataclass(frozen=True)
class ExpectedArtifact:
    name: str
    relative_path: str
    package_name: str
    launchable_activity: str
    debuggable: bool
    extract_native_libs: bool
    native_libraries_stored: bool
    query_packages: tuple[str, ...] | None = None
    public_monogame_payload_sha256: str | None = None
    public_openal_sha256: str | None = None
    require_smapi_host: bool = False


@dataclass(frozen=True)
class ManifestElement:
    path: tuple[str, ...]
    attributes: dict[str, str]


@dataclass(frozen=True)
class ParsedManifest:
    query_packages: list[str]
    permissions: list[str]
    activities: dict[str, dict[str, str]]


def run(*command: str) -> str:
    process = subprocess.run(command, text=True, capture_output=True, check=False)
    if process.returncode != 0:
        raise RuntimeError(
            f"Command failed ({process.returncode}): {' '.join(command)}\n"
            f"stdout:\n{process.stdout}\nstderr:\n{process.stderr}"
        )
    return process.stdout


def sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def capture(pattern: str, text: str, label: str) -> str:
    match = re.search(pattern, text, re.MULTILINE)
    if not match:
        raise RuntimeError(f"Could not parse {label} from aapt output.")
    return match.group(1)


def capture_manifest_bool(attribute: str, xmltree: str) -> bool:
    match = re.search(
        rf"A: android:{re.escape(attribute)}\([^)]*\)=\(type 0x12\)(0x[0-9a-fA-F]+)",
        xmltree,
    )
    if not match:
        raise RuntimeError(f"Could not parse android:{attribute} from manifest xmltree.")
    return int(match.group(1), 16) != 0


def parse_xmltree_string(raw_value: str) -> str | None:
    raw_match = re.search(r'\(Raw:\s*"([^"]*)"\)\s*$', raw_value)
    if raw_match:
        return raw_match.group(1)
    string_match = re.match(r'"([^"]*)"', raw_value)
    if string_match:
        return string_match.group(1)
    bool_match = re.match(r"\(type 0x12\)(0x[0-9a-fA-F]+)", raw_value)
    if bool_match:
        return "true" if int(bool_match.group(1), 16) != 0 else "false"
    return None


def parse_manifest(xmltree: str) -> ParsedManifest:
    elements: list[ManifestElement] = []
    stack: list[tuple[int, str]] = []
    current_attributes: dict[str, str] | None = None

    for line in xmltree.splitlines():
        element_match = re.match(r"^(\s*)E:\s+([^\s(]+)", line)
        if element_match:
            indent = len(element_match.group(1))
            name = element_match.group(2)
            while stack and stack[-1][0] >= indent:
                stack.pop()
            stack.append((indent, name))
            current_attributes = {}
            elements.append(ManifestElement(tuple(value for _, value in stack), current_attributes))
            continue

        attribute_match = re.match(r"^(\s*)A:\s+([^\s(=]+)(?:\([^)]*\))?=(.*)$", line)
        if not attribute_match or current_attributes is None:
            continue
        value = parse_xmltree_string(attribute_match.group(3))
        if value is not None:
            current_attributes[attribute_match.group(2)] = value

    if not elements or elements[0].path != ("manifest",):
        raise RuntimeError("Could not parse the manifest element hierarchy from aapt xmltree output.")

    query_packages: list[str] = []
    permissions: list[str] = []
    activities: dict[str, dict[str, str]] = {}
    for element in elements:
        android_name = element.attributes.get("android:name")
        if android_name is None:
            continue
        if element.path == ("manifest", "queries", "package"):
            query_packages.append(android_name)
        elif (
            len(element.path) == 2
            and element.path[0] == "manifest"
            and element.path[1].startswith("uses-permission")
        ):
            permissions.append(android_name)
        elif element.path == ("manifest", "application", "activity"):
            activities[android_name] = element.attributes

    return ParsedManifest(query_packages, permissions, activities)


def parse_badging_permissions(badging: str) -> list[str]:
    return re.findall(r"^uses-permission(?:-sdk-[^:]*)?:\s+name='([^']+)'", badging, re.MULTILINE)


def read_elf64_aarch64_section(image: bytes, section_name: str) -> bytes:
    if (
        len(image) < 64
        or image[:4] != b"\x7fELF"
        or image[4] != 2
        or image[5] != 1
        or image[6] != 1
        or struct.unpack_from("<H", image, 18)[0] != 183
        or struct.unpack_from("<I", image, 20)[0] != 1
    ):
        raise RuntimeError("Managed provider wrapper is not a supported ELF64 little-endian AArch64 image.")

    section_offset = struct.unpack_from("<Q", image, 40)[0]
    section_entry_size = struct.unpack_from("<H", image, 58)[0]
    section_count = struct.unpack_from("<H", image, 60)[0]
    string_table_index = struct.unpack_from("<H", image, 62)[0]
    if (
        section_entry_size < 64
        or section_count < 1
        or section_count > 4096
        or string_table_index >= section_count
        or section_offset > len(image)
        or section_count * section_entry_size > len(image) - section_offset
    ):
        raise RuntimeError("Managed provider ELF section metadata is invalid or exceeds bounds.")

    def section_header(index: int) -> tuple[int, int, int]:
        offset = section_offset + index * section_entry_size
        return (
            struct.unpack_from("<I", image, offset)[0],
            struct.unpack_from("<Q", image, offset + 24)[0],
            struct.unpack_from("<Q", image, offset + 32)[0],
        )

    _, names_offset, names_size = section_header(string_table_index)
    if names_offset > len(image) or names_size > len(image) - names_offset:
        raise RuntimeError("Managed provider ELF section-name table is out of bounds.")
    names = image[names_offset : names_offset + names_size]

    for index in range(section_count):
        name_offset, payload_offset, payload_size = section_header(index)
        if name_offset >= len(names):
            raise RuntimeError("Managed provider ELF section name is out of bounds.")
        terminator = names.find(b"\0", name_offset)
        if terminator < 0:
            raise RuntimeError("Managed provider ELF section name is unterminated.")
        name = names[name_offset:terminator].decode("ascii", errors="strict")
        if name != section_name:
            continue
        if payload_offset > len(image) or payload_size > len(image) - payload_offset:
            raise RuntimeError("Managed provider ELF payload section is out of bounds.")
        return image[payload_offset : payload_offset + payload_size]

    raise RuntimeError(f"Managed provider ELF does not contain the required {section_name!r} section.")


def verify_smapi_bundle_manifest(archive: zipfile.ZipFile) -> dict[str, object]:
    manifest_path = "assets/smapi-bundle-manifest.json"
    try:
        manifest = json.loads(archive.read(manifest_path))
    except KeyError as exception:
        raise RuntimeError("The APK does not contain the generated SMAPI bundle manifest.") from exception
    if not isinstance(manifest, dict) or set(manifest) != {"schema", "bundleId", "contentSha256", "files"}:
        raise RuntimeError("The generated SMAPI bundle manifest shape is invalid.")
    if manifest["schema"] != "junimogate-smapi-bundle/v2":
        raise RuntimeError("The generated SMAPI bundle manifest schema is invalid.")
    content_sha256 = manifest["contentSha256"]
    bundle_id = manifest["bundleId"]
    if not isinstance(content_sha256, str) or re.fullmatch(r"[0-9a-f]{64}", content_sha256) is None:
        raise RuntimeError("The generated SMAPI bundle content digest is invalid.")
    if bundle_id != f"smapi-bundle-{content_sha256[:24]}":
        raise RuntimeError("The generated SMAPI bundle ID does not match its content digest.")
    files = manifest["files"]
    if not isinstance(files, list) or not files or len(files) > 256:
        raise RuntimeError("The generated SMAPI bundle file list is invalid.")

    archive_names = set(archive.namelist())
    asset_paths: set[str] = set()
    relative_paths: set[str] = set()
    previous_asset_path: str | None = None
    for index, entry in enumerate(files):
        if not isinstance(entry, dict) or set(entry) != {"assetPath", "relativePath", "size", "sha256"}:
            raise RuntimeError(f"SMAPI bundle entry {index} has an invalid shape.")
        asset_path = entry["assetPath"]
        relative_path = entry["relativePath"]
        size = entry["size"]
        file_sha256 = entry["sha256"]
        if (
            not isinstance(asset_path, str)
            or not isinstance(relative_path, str)
            or not isinstance(size, int)
            or isinstance(size, bool)
            or size <= 0
            or not isinstance(file_sha256, str)
            or re.fullmatch(r"[0-9a-f]{64}", file_sha256) is None
            or pathlib.PurePosixPath(asset_path).is_absolute()
            or pathlib.PurePosixPath(relative_path).is_absolute()
            or any(part in {"", ".", ".."} for part in pathlib.PurePosixPath(asset_path).parts)
            or any(part in {"", ".", ".."} for part in pathlib.PurePosixPath(relative_path).parts)
            or "\\" in asset_path
            or "\\" in relative_path
            or asset_path.casefold() in asset_paths
            or relative_path.casefold() in relative_paths
            or previous_asset_path is not None
            and previous_asset_path >= asset_path
        ):
            raise RuntimeError(f"SMAPI bundle entry {index} is invalid.")
        archive_path = f"assets/{asset_path}"
        if archive_path not in archive_names:
            raise RuntimeError(f"SMAPI bundle asset is missing from the APK: {archive_path}")
        payload = archive.read(archive_path)
        if len(payload) != size or hashlib.sha256(payload).hexdigest() != file_sha256:
            raise RuntimeError(f"SMAPI bundle asset does not match its manifest: {archive_path}")
        asset_paths.add(asset_path.casefold())
        relative_paths.add(relative_path.casefold())
        previous_asset_path = asset_path

    identity_payload = json.dumps(
        {"schema": manifest["schema"], "files": files},
        ensure_ascii=True,
        separators=(",", ":"),
        sort_keys=True,
    ).encode("utf-8")
    if hashlib.sha256(identity_payload).hexdigest() != content_sha256:
        raise RuntimeError("The generated SMAPI bundle content digest is incorrect.")
    packaged_assets = {
        name.removeprefix("assets/")
        for name in archive_names
        if name.startswith("assets/smapi-managed/") or name.startswith("assets/smapi-internal/")
    }
    if packaged_assets != {entry["assetPath"] for entry in files}:
        raise RuntimeError("The generated SMAPI bundle manifest does not cover the exact packaged asset set.")
    return {"bundleId": bundle_id, "contentSha256": content_sha256, "fileCount": len(files)}


def verify_artifact(root: pathlib.Path, aapt: pathlib.Path, apksigner: pathlib.Path, expected: ExpectedArtifact) -> dict[str, object]:
    path = root / expected.relative_path
    if not path.is_file():
        raise FileNotFoundError(f"Missing APK: {path}")

    badging = run(str(aapt), "dump", "badging", str(path))
    manifest_tree = run(str(aapt), "dump", "xmltree", str(path), "AndroidManifest.xml")
    signatures = run(str(apksigner), "verify", "--verbose", "--print-certs", str(path))

    package_name = capture(r"^package: name='([^']+)'", badging, "package name")
    version_code = capture(r"^package: .* versionCode='([^']+)'", badging, "version code")
    version_name = capture(r"^package: .* versionName='([^']+)'", badging, "version name")
    compile_sdk = capture(r"^package: .* compileSdkVersion='([^']+)'", badging, "compile SDK")
    min_sdk = capture(r"^sdkVersion:'([^']+)'", badging, "minimum SDK")
    target_sdk = capture(r"^targetSdkVersion:'([^']+)'", badging, "target SDK")
    activity = capture(r"^launchable-activity: name='([^']+)'", badging, "launchable activity")
    debuggable = bool(re.search(r"^application-debuggable$", badging, re.MULTILINE))
    extract_native_libs = capture_manifest_bool("extractNativeLibs", manifest_tree)
    manifest = parse_manifest(manifest_tree)
    badging_permissions = parse_badging_permissions(badging)
    declared_permissions = sorted(set(manifest.permissions) | set(badging_permissions))
    broad_storage_permissions = sorted(
        set(declared_permissions)
        & {
            "android.permission.READ_EXTERNAL_STORAGE",
            "android.permission.WRITE_EXTERNAL_STORAGE",
            "android.permission.MANAGE_EXTERNAL_STORAGE",
        }
    )
    privacy_sensitive_permissions = sorted(
        set(declared_permissions)
        & {
            "android.permission.READ_PHONE_STATE",
        }
    )

    with zipfile.ZipFile(path) as archive:
        archive_names = archive.namelist()
        smapi_bundle = verify_smapi_bundle_manifest(archive) if expected.require_smapi_host else None
        native_entries = [
            entry
            for entry in archive.infolist()
            if entry.filename.startswith("lib/") and not entry.is_dir()
        ]
        mono_runtime_entries = [
            name
            for name in archive_names
            if pathlib.PurePosixPath(name).name.casefold() == "libmonosgen-2.0.so"
        ]
        patched_mono_runtime = None
        if mono_runtime_entries == ["lib/arm64-v8a/libmonosgen-2.0.so"]:
            patched_mono_runtime = archive.read(mono_runtime_entries[0])
        native_abis = sorted(
            {
                parts[1]
                for entry in native_entries
                if len(parts := entry.filename.split("/")) >= 3
            }
        )
        game_aot_markers = [
            name
            for name in archive_names
            if pathlib.PurePosixPath(name).name.casefold().startswith("libaot-")
        ]
        mono_game_entries = [
            name for name in archive_names if "monogame.framework.dll" in name.casefold()
        ]
        expected_mono_game_entry = "lib/arm64-v8a/lib_MonoGame.Framework.dll.so"
        public_monogame_payload_sha256 = None
        if mono_game_entries == [expected_mono_game_entry]:
            mono_game_elf = archive.read(expected_mono_game_entry)
            mono_game_payload = read_elf64_aarch64_section(mono_game_elf, "payload")
            if not mono_game_payload.startswith(b"MZ"):
                raise RuntimeError("The public MonoGame provider payload is not a managed PE image.")
            public_monogame_payload_sha256 = hashlib.sha256(mono_game_payload).hexdigest()

        expected_openal_entry = "lib/arm64-v8a/libopenal32.so"
        openal_entries = [
            name
            for name in archive_names
            if pathlib.PurePosixPath(name).name.casefold() == "libopenal32.so"
        ]
        public_openal_sha256 = None
        if openal_entries == [expected_openal_entry]:
            public_openal_sha256 = hashlib.sha256(archive.read(expected_openal_entry)).hexdigest()

        mono_game_provider_valid = (
            public_monogame_payload_sha256 == expected.public_monogame_payload_sha256
            if expected.public_monogame_payload_sha256 is not None
            else not mono_game_entries
        )
        openal_provider_valid = (
            public_openal_sha256 == expected.public_openal_sha256
            if expected.public_openal_sha256 is not None
            else not openal_entries
        )
        prohibited_game_payload_markers = [
            name
            for name in archive_names
            if (
                "stardewvalley.dll" in name.casefold()
                or "stardewvalley.gamedata.dll" in name.casefold()
                or pathlib.PurePosixPath(name).name.casefold() in {
                    "lib_bmfont.dll.so",
                    "lib_lidgren.network.dll.so",
                    "lib_xtile.dll.so",
                }
                or "assets/content/" in name.casefold()
                or name in game_aot_markers
                or ("monogame.framework.dll" in name.casefold() and not mono_game_provider_valid)
            )
        ]
        native_libraries_stored = bool(native_entries) and all(
            entry.compress_type == zipfile.ZIP_STORED for entry in native_entries
        )
        compressed_native_entries = [
            entry.filename for entry in native_entries if entry.compress_type != zipfile.ZIP_STORED
        ]
        smapi_managed_payloads = {
            "lib/arm64-v8a/lib_StardewModdingAPI.dll.so",
            "lib/arm64-v8a/lib_SMAPI.Toolkit.dll.so",
            "lib/arm64-v8a/lib_SMAPI.Toolkit.CoreInterfaces.dll.so",
        }
        smapi_cecil_assets = {
            "assets/smapi-managed/0Harmony.dll",
            "assets/smapi-managed/DeepCloner.dll",
            "assets/smapi-managed/HtmlAgilityPack.dll",
            "assets/smapi-managed/Markdig.dll",
            "assets/smapi-managed/Microsoft.Extensions.DependencyInjection.Abstractions.dll",
            "assets/smapi-managed/Mono.Cecil.dll",
            "assets/smapi-managed/Mono.Cecil.Mdb.dll",
            "assets/smapi-managed/Mono.Cecil.Pdb.dll",
            "assets/smapi-managed/Mono.Cecil.Rocks.dll",
            "assets/smapi-managed/NVorbis.dll",
            "assets/smapi-managed/Newtonsoft.Json.dll",
            "assets/smapi-managed/Newtonsoft.Json.Bson.dll",
            "assets/smapi-managed/Pintail.dll",
            "assets/smapi-managed/SMAPI.Toolkit.dll",
            "assets/smapi-managed/SMAPI.Toolkit.CoreInterfaces.dll",
            "assets/smapi-managed/SkiaSharp.dll",
            "assets/smapi-managed/StardewModdingAPI.dll",
            "assets/smapi-managed/StbImageWriteSharp.dll",
            "assets/smapi-managed/TMXTile.dll",
            "assets/smapi-managed/TextCopy.dll",
        }
        legacy_smapi_toolkit_payloads = {
            "lib/arm64-v8a/lib_StardewModdingAPI.Toolkit.dll.so",
            "lib/arm64-v8a/lib_StardewModdingAPI.Toolkit.CoreInterfaces.dll.so",
            "assets/smapi-managed/StardewModdingAPI.Toolkit.dll",
            "assets/smapi-managed/StardewModdingAPI.Toolkit.CoreInterfaces.dll",
        }
        excluded_smapi_update_clients = {
            "pathoschild.http.client",
            "system.net.http.formatting",
        }
        smapi_update_client_payloads = [
            name
            for name in archive_names
            if any(marker in name.casefold() for marker in excluded_smapi_update_clients)
        ]
        smapi_internal_assets = {
            "assets/smapi-internal/config.json",
            "assets/smapi-internal/metadata.json",
            "assets/smapi-internal/blacklist.json",
            "assets/smapi-internal/i18n/default.json",
        }
        smapi_payload_complete = (
            smapi_managed_payloads.issubset(archive_names)
            and smapi_cecil_assets.issubset(archive_names)
            and smapi_internal_assets.issubset(archive_names)
            and legacy_smapi_toolkit_payloads.isdisjoint(archive_names)
            and smapi_bundle is not None
        )

    smapi_activity = manifest.activities.get("org.junimogate.gamehost.SmapiGameActivity")

    v2 = "Verified using v2 scheme (APK Signature Scheme v2): true" in signatures
    v3 = "Verified using v3 scheme (APK Signature Scheme v3): true" in signatures
    certificate_sha256 = capture(r"^Signer #1 certificate SHA-256 digest: ([0-9a-f]+)$", signatures, "certificate SHA-256")

    checks = {
        "package": package_name == expected.package_name,
        "activity": activity == expected.launchable_activity,
        "compileSdk35": compile_sdk == "35",
        "targetSdk35": target_sdk == "35",
        "minSdk26": min_sdk == "26",
        "arm64Only": native_abis == ["arm64-v8a"],
        "debuggable": debuggable == expected.debuggable,
        "extractNativeLibs": extract_native_libs == expected.extract_native_libs,
        "nativeLibrariesStored": native_libraries_stored == expected.native_libraries_stored,
        "singleApplicationMonoRuntime": mono_runtime_entries == ["lib/arm64-v8a/libmonosgen-2.0.so"],
        "monoAccessPolicyPatched": patched_mono_runtime is not None
        and patched_mono_runtime.count(b"\x20\x00\x80\x52\xc0\x03\x5f\xd6") >= 2,
        "monoReflectionEmitFallbackPatched": patched_mono_runtime is not None
        and patched_mono_runtime.count(
            b"\x1f\x01\x00\xf1\x20\x01\x88\x9a\xfd\x7b\xc1\xa8\xc0\x03\x5f\xd6"
        ) == 1,
        "signatureV2": v2,
        "signatureV3": v3,
        "manifestPermissionsAgreeWithBadging": sorted(manifest.permissions) == sorted(badging_permissions),
        "noQueryAllPackages": "android.permission.QUERY_ALL_PACKAGES" not in declared_permissions,
        "noBroadStoragePermissions": not broad_storage_permissions,
        "noUnneededPrivacySensitivePermissions": not privacy_sensitive_permissions,
        "noCommercialGamePayload": not prohibited_game_payload_markers,
        "noGameAotPayload": not game_aot_markers,
        "publicMonoGameProvider": mono_game_provider_valid,
        "publicOpenAlProvider": openal_provider_valid,
    }
    if expected.require_smapi_host:
        checks["smapiPayloadComplete"] = smapi_payload_complete
        checks["noSmapiUpdateClientPayloads"] = not smapi_update_client_payloads
        checks["smapiActivityIsolated"] = (
            smapi_activity is not None
            and smapi_activity.get("android:exported") in {"false", "0"}
            and smapi_activity.get("android:process") == ":game"
        )
        checks["appLocalesDeclared"] = (
            "android:localeConfig" in manifest_tree
            and "androidx.appcompat.app.AppLocalesMetadataHolderService" in manifest_tree
            and "autoStoreLocales" in manifest_tree
            and "res/xml/locales_config.xml" in archive_names
        )
    if expected.query_packages is not None:
        checks["exactGamePackageQueries"] = (
            sorted(manifest.query_packages) == sorted(expected.query_packages)
            and len(manifest.query_packages) == len(expected.query_packages)
        )
    failures = [name for name, passed in checks.items() if not passed]
    if failures:
        raise RuntimeError(f"{expected.name} failed checks: {', '.join(failures)}")

    return {
        "name": expected.name,
        "path": expected.relative_path,
        "size": path.stat().st_size,
        "sha256": sha256(path),
        "packageName": package_name,
        "versionCode": version_code,
        "versionName": version_name,
        "compileSdk": compile_sdk,
        "minSdk": min_sdk,
        "targetSdk": target_sdk,
        "launchableActivity": activity,
        "debuggable": debuggable,
        "extractNativeLibs": extract_native_libs,
        "nativeAbis": native_abis,
        "nativeEntryCount": len(native_entries),
        "nativeLibrariesStored": native_libraries_stored,
        "singleApplicationMonoRuntime": mono_runtime_entries == ["lib/arm64-v8a/libmonosgen-2.0.so"],
        "monoAccessPolicyPatched": patched_mono_runtime is not None
        and patched_mono_runtime.count(b"\x20\x00\x80\x52\xc0\x03\x5f\xd6") >= 2,
        "monoReflectionEmitFallbackPatched": patched_mono_runtime is not None
        and patched_mono_runtime.count(
            b"\x1f\x01\x00\xf1\x20\x01\x88\x9a\xfd\x7b\xc1\xa8\xc0\x03\x5f\xd6"
        ) == 1,
        "compressedNativeEntries": compressed_native_entries,
        "publicRuntimeProviders": {
            "monoGameEntry": mono_game_entries[0] if len(mono_game_entries) == 1 else None,
            "monoGamePayloadSha256": public_monogame_payload_sha256,
            "openAlEntry": openal_entries[0] if len(openal_entries) == 1 else None,
            "openAlSha256": public_openal_sha256,
        },
        "smapiHost": {
            "required": expected.require_smapi_host,
            "payloadComplete": smapi_payload_complete,
            "activity": smapi_activity,
            "bundle": smapi_bundle,
        },
        "manifest": {
            "queryPackages": manifest.query_packages,
            "declaredPermissions": declared_permissions,
            "xmltreeDeclaredPermissions": manifest.permissions,
            "badgingDeclaredPermissions": badging_permissions,
            "broadStoragePermissions": broad_storage_permissions,
            "privacySensitivePermissions": privacy_sensitive_permissions,
        },
        "signature": {
            "v2": v2,
            "v3": v3,
            "certificateSha256": certificate_sha256,
            "developmentCertificate": True,
        },
        "checks": checks,
    }


def main() -> int:
    root = pathlib.Path(os.environ["JUNIMOGATE_ROOT"])
    android_sdk = pathlib.Path(os.environ["ANDROID_SDK_ROOT"])
    build_tools = os.environ["JUNIMOGATE_ANDROID_BUILD_TOOLS_VERSION"]
    output = pathlib.Path(os.environ["JUNIMOGATE_APK_REPORT"])
    aapt = android_sdk / f"build-tools/{build_tools}/aapt"
    apksigner = android_sdk / f"build-tools/{build_tools}/apksigner"

    artifacts = [
        ExpectedArtifact(
            "runtime-probe-debug",
            "tools/JunimoGate.RuntimeProbe/bin/Debug/net9.0-android35.0/android-arm64/org.junimogate.runtimeprobe-Signed.apk",
            "org.junimogate.runtimeprobe",
            "org.junimogate.runtimeprobe.MainActivity",
            True,
            False,
            True,
        ),
        ExpectedArtifact(
            "runtime-probe-release",
            "tools/JunimoGate.RuntimeProbe/bin/Release/net9.0-android35.0/android-arm64/org.junimogate.runtimeprobe-Signed.apk",
            "org.junimogate.runtimeprobe",
            "org.junimogate.runtimeprobe.MainActivity",
            True,
            False,
            True,
        ),
        ExpectedArtifact(
            "app-debug",
            "src/JunimoGate.App/bin/Debug/net9.0-android35.0/android-arm64/org.junimogate.app-Signed.apk",
            "org.junimogate.app",
            "org.junimogate.app.MainActivity",
            True,
            False,
            True,
            (
                "com.chucklefish.stardewvalley",
                "com.chucklefish.stardewvalleysamsung",
            ),
            "33a406a56f43f74d61a155645483ade9ec698a19b19884ce10462068de93427c",
            "c972352d7f72966ad3b05be42a425925e8d28efd88a6d0f0e726f6b1cf4bb6d0",
            True,
        ),
        ExpectedArtifact(
            "app-release",
            "src/JunimoGate.App/bin/Release/net9.0-android35.0/android-arm64/org.junimogate.app-Signed.apk",
            "org.junimogate.app",
            "org.junimogate.app.MainActivity",
            False,
            False,
            True,
            (
                "com.chucklefish.stardewvalley",
                "com.chucklefish.stardewvalleysamsung",
            ),
            "33a406a56f43f74d61a155645483ade9ec698a19b19884ce10462068de93427c",
            "c972352d7f72966ad3b05be42a425925e8d28efd88a6d0f0e726f6b1cf4bb6d0",
            True,
        ),
    ]

    report = {
        "schemaVersion": 1,
        "generatedAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "passed",
        "note": "All artifacts use the Android development certificate. This is build validation, not production signing or device runtime validation.",
        "expectedArtifacts": [asdict(value) for value in artifacts],
        "artifacts": [verify_artifact(root, aapt, apksigner, value) for value in artifacts],
    }

    output.parent.mkdir(parents=True, exist_ok=True)
    encoded = json.dumps(report, indent=2, ensure_ascii=False) + "\n"
    with tempfile.NamedTemporaryFile("w", encoding="utf-8", dir=output.parent, delete=False) as stream:
        temporary = pathlib.Path(stream.name)
        stream.write(encoded)
    temporary.replace(output)
    print(encoded, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
