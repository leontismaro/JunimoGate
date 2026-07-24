#!/usr/bin/env python3
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
import pathlib
import platform
import subprocess
import tempfile
from typing import Any


def run(*command: str) -> dict[str, Any]:
    process = subprocess.run(command, text=True, capture_output=True, check=False)
    return {
        "command": list(command),
        "exitCode": process.returncode,
        "stdout": process.stdout.strip(),
        "stderr": process.stderr.strip(),
    }


def sha256(path: pathlib.Path) -> str | None:
    if not path.is_file():
        return None
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def executable_identity(path: pathlib.Path) -> dict[str, Any]:
    resolved = path.resolve()
    return {
        "path": str(path),
        "resolvedPath": str(resolved),
        "sha256": sha256(resolved),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()

    dotnet_root = pathlib.Path(os.environ["DOTNET_ROOT"])
    java_home = pathlib.Path(os.environ["JAVA_HOME"])
    android_sdk = pathlib.Path(os.environ["ANDROID_SDK_ROOT"])
    cli_version = os.environ["JUNIMOGATE_ANDROID_CLI_VERSION"]
    build_tools_version = os.environ["JUNIMOGATE_ANDROID_BUILD_TOOLS_VERSION"]

    executables = {
        "dotnet": dotnet_root / "dotnet",
        "java": java_home / "bin/java",
        "javac": java_home / "bin/javac",
        "sdkmanager": android_sdk / f"cmdline-tools/{cli_version}/bin/sdkmanager",
        "adb": android_sdk / "platform-tools/adb",
        "aapt2": android_sdk / f"build-tools/{build_tools_version}/aapt2",
        "d8": android_sdk / f"build-tools/{build_tools_version}/d8",
        "apksigner": android_sdk / f"build-tools/{build_tools_version}/apksigner",
    }

    report = {
        "schemaVersion": 1,
        "generatedAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "host": {
            "platform": platform.platform(),
            "machine": platform.machine(),
            "python": platform.python_version(),
        },
        "expected": {
            "dotnetSdk": os.environ["JUNIMOGATE_DOTNET_SDK_VERSION"],
            "jdk": os.environ["JUNIMOGATE_JDK_VERSION"],
            "androidCommandLineTools": cli_version,
            "androidPlatform": os.environ["JUNIMOGATE_ANDROID_PLATFORM_VERSION"],
            "androidBuildTools": build_tools_version,
        },
        "roots": {
            "dotnet": str(dotnet_root),
            "java": str(java_home),
            "androidSdk": str(android_sdk),
            "androidUserHome": os.environ.get("ANDROID_USER_HOME"),
            "dotnetCliHome": os.environ.get("DOTNET_CLI_HOME"),
        },
        "executables": {name: executable_identity(path) for name, path in executables.items()},
        "commands": {
            "dotnetVersion": run(str(executables["dotnet"]), "--version"),
            "dotnetInfo": run(str(executables["dotnet"]), "--info"),
            "workloads": run(str(executables["dotnet"]), "workload", "list"),
            "java": run(str(executables["java"]), "-version"),
            "javac": run(str(executables["javac"]), "-version"),
            "sdkPackages": run(str(executables["sdkmanager"]), "--sdk_root=" + str(android_sdk), "--list_installed"),
            "adbVersion": run(str(executables["adb"]), "version"),
            "adbDevices": run(str(executables["adb"]), "devices", "-l"),
        },
    }

    output = pathlib.Path(args.output)
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
