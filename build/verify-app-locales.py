#!/usr/bin/env python3
from __future__ import annotations

import pathlib
import re
import sys
import xml.etree.ElementTree as ET


ROOT = pathlib.Path(__file__).resolve().parents[1]
DEFAULT = ROOT / "src/JunimoGate.App/Resources/values/strings.xml"
CHINESE = ROOT / "src/JunimoGate.App/Resources/values-zh-rCN/strings.xml"
LOCALES = ROOT / "src/JunimoGate.App/Resources/xml/locales_config.xml"
PLACEHOLDER = re.compile(r"%(\d+)\$([a-zA-Z])")


def read_resources(path: pathlib.Path) -> dict[tuple[str, str, str], str]:
    root = ET.parse(path).getroot()
    resources: dict[tuple[str, str, str], str] = {}
    for element in root:
        name = element.attrib.get("name")
        if not name or element.tag not in {"string", "plurals"}:
            raise RuntimeError(f"Unsupported or unnamed resource in {path}: {element.tag}")
        if element.tag == "string":
            resources[("string", name, "")] = "".join(element.itertext())
            continue
        quantities = {item.attrib.get("quantity") for item in element}
        if None in quantities or not quantities:
            raise RuntimeError(f"Invalid plurals resource in {path}: {name}")
        for item in element:
            resources[("plurals", name, item.attrib["quantity"])] = "".join(item.itertext())
    return resources


def placeholders(value: str) -> tuple[tuple[int, str], ...]:
    return tuple(sorted((int(index), kind) for index, kind in PLACEHOLDER.findall(value)))


def main() -> int:
    default = read_resources(DEFAULT)
    chinese = read_resources(CHINESE)
    if default.keys() != chinese.keys():
        missing = sorted(default.keys() - chinese.keys())
        extra = sorted(chinese.keys() - default.keys())
        raise RuntimeError(f"Locale resource keys differ. missing={missing} extra={extra}")
    for key in default:
        if placeholders(default[key]) != placeholders(chinese[key]):
            raise RuntimeError(
                f"Locale format placeholders differ for {key}: "
                f"default={placeholders(default[key])} zh-CN={placeholders(chinese[key])}"
            )

    locale_root = ET.parse(LOCALES).getroot()
    android_name = "{http://schemas.android.com/apk/res/android}name"
    declared = {item.attrib.get(android_name) for item in locale_root}
    if declared != {"en", "zh-CN"}:
        raise RuntimeError(f"Unexpected application locale set: {sorted(value for value in declared if value)}")

    print(f"Verified {len(default)} localized resources for en and zh-CN.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ET.ParseError, RuntimeError) as exception:
        print(f"Locale verification failed: {exception}", file=sys.stderr)
        raise SystemExit(1) from exception
