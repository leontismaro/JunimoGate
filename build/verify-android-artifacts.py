#!/usr/bin/env python3
from __future__ import annotations

import datetime as dt
import hashlib
import json
import os
import pathlib
import re
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


@dataclass(frozen=True)
class ManifestElement:
    path: tuple[str, ...]
    attributes: dict[str, str]


@dataclass(frozen=True)
class ParsedManifest:
    query_packages: list[str]
    permissions: list[str]


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
    return string_match.group(1) if string_match else None


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

    return ParsedManifest(query_packages, permissions)


def parse_badging_permissions(badging: str) -> list[str]:
    return re.findall(r"^uses-permission(?:-sdk-[^:]*)?:\s+name='([^']+)'", badging, re.MULTILINE)


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
        native_entries = [
            entry
            for entry in archive.infolist()
            if entry.filename.startswith("lib/") and not entry.is_dir()
        ]
        native_abis = sorted(
            {
                parts[1]
                for entry in native_entries
                if len(parts := entry.filename.split("/")) >= 3
            }
        )
        commercial_markers = [
            name
            for name in archive.namelist()
            if any(marker in name.casefold() for marker in ("stardewvalley.dll", "assets/content/", "libaot-stardewvalley"))
        ]
        native_libraries_stored = bool(native_entries) and all(
            entry.compress_type == zipfile.ZIP_STORED for entry in native_entries
        )
        compressed_native_entries = [
            entry.filename for entry in native_entries if entry.compress_type != zipfile.ZIP_STORED
        ]

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
        "signatureV2": v2,
        "signatureV3": v3,
        "manifestPermissionsAgreeWithBadging": sorted(manifest.permissions) == sorted(badging_permissions),
        "noQueryAllPackages": "android.permission.QUERY_ALL_PACKAGES" not in declared_permissions,
        "noBroadStoragePermissions": not broad_storage_permissions,
        "noUnneededPrivacySensitivePermissions": not privacy_sensitive_permissions,
        "noCommercialGamePayload": not commercial_markers,
    }
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
        "compressedNativeEntries": compressed_native_entries,
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
