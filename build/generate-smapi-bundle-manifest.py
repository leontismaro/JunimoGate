#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import tempfile


SCHEMA = "junimogate-smapi-bundle/v2"
BUNDLE_ID_PREFIX = "smapi-bundle-"


def sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def require_relative_path(value: str, label: str) -> None:
    path = pathlib.PurePosixPath(value)
    if (
        not value
        or "\\" in value
        or path.is_absolute()
        or any(part in {"", ".", ".."} for part in path.parts)
        or str(path) != value
    ):
        raise ValueError(f"Invalid {label}: {value!r}")


def load_assets(spec_path: pathlib.Path) -> list[dict[str, object]]:
    entries: list[dict[str, object]] = []
    asset_paths: set[str] = set()
    relative_paths: set[str] = set()
    for line_number, raw_line in enumerate(spec_path.read_text(encoding="utf-8").splitlines(), 1):
        if not raw_line:
            continue
        fields = raw_line.split("|", 2)
        if len(fields) != 3:
            raise ValueError(f"Invalid bundle spec line {line_number}.")
        source_text, asset_path, relative_path = fields
        source = pathlib.Path(source_text).resolve()
        if not source.is_file():
            raise FileNotFoundError(f"SMAPI bundle source does not exist: {source}")
        require_relative_path(asset_path, "asset path")
        require_relative_path(relative_path, "deployed path")
        asset_key = asset_path.casefold()
        relative_key = relative_path.casefold()
        if asset_key in asset_paths:
            raise ValueError(f"Duplicate SMAPI bundle asset path: {asset_path}")
        if relative_key in relative_paths:
            raise ValueError(f"Duplicate SMAPI bundle deployed path: {relative_path}")
        asset_paths.add(asset_key)
        relative_paths.add(relative_key)
        size = source.stat().st_size
        if size <= 0:
            raise ValueError(f"SMAPI bundle source is empty: {source}")
        entries.append(
            {
                "assetPath": asset_path,
                "relativePath": relative_path,
                "size": size,
                "sha256": sha256(source),
            }
        )
    if not entries:
        raise ValueError("The SMAPI bundle cannot be empty.")
    return sorted(entries, key=lambda entry: str(entry["assetPath"]))


def write_if_changed(path: pathlib.Path, content: bytes) -> None:
    if path.is_file() and path.read_bytes() == content:
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as stream:
        temporary = pathlib.Path(stream.name)
        stream.write(content)
        stream.flush()
    temporary.replace(path)


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate the immutable SMAPI Android bundle manifest.")
    parser.add_argument("--spec", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()

    entries = load_assets(args.spec)
    identity_payload = json.dumps(
        {"schema": SCHEMA, "files": entries},
        ensure_ascii=True,
        separators=(",", ":"),
        sort_keys=True,
    ).encode("utf-8")
    content_sha256 = hashlib.sha256(identity_payload).hexdigest()
    manifest = {
        "schema": SCHEMA,
        "bundleId": BUNDLE_ID_PREFIX + content_sha256[:24],
        "contentSha256": content_sha256,
        "files": entries,
    }
    output = (json.dumps(manifest, ensure_ascii=True, indent=2) + "\n").encode("utf-8")
    write_if_changed(args.output, output)


if __name__ == "__main__":
    main()
