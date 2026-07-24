#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
"$root/build/run-runtime-probe.sh" Debug
"$root/build/run-runtime-probe.sh" Release
