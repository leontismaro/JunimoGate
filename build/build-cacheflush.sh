#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=cacheflush-versions.sh
source "$root/build/cacheflush-versions.sh"

toolchains="${JUNIMOGATE_TOOLCHAINS_DIR:-$root/.toolchains}"
zig="$toolchains/zig-$ZIG_VERSION/zig"
source_file="$root/native/JunimoGate.CacheFlush/clear_cache.S"
output_dir="$root/artifacts/native-cacheflush"
output="$output_dir/$CACHEFLUSH_LIBRARY_NAME"
cache_root="$toolchains/zig-cache"

[[ -x "$zig" ]] || {
  printf 'Missing pinned Zig %s at %s\n' "$ZIG_VERSION" "$zig" >&2
  printf 'Expected archive: %s\nSHA-256: %s\n' "$ZIG_URL" "$ZIG_SHA256" >&2
  exit 1
}
[[ "$($zig version)" == "$ZIG_VERSION" ]] || {
  printf 'Unexpected Zig version at %s\n' "$zig" >&2
  exit 1
}

mkdir -p "$output_dir" "$cache_root/global" "$cache_root/local"
export ZIG_GLOBAL_CACHE_DIR="$cache_root/global"
export ZIG_LOCAL_CACHE_DIR="$cache_root/local"

temporary="$output.tmp"
trap 'rm -f "$temporary"' EXIT
"$zig" cc \
  -target aarch64-linux-none \
  -shared -nostdlib -g0 \
  -Wl,-soname,"$CACHEFLUSH_LIBRARY_NAME" \
  -Wl,-z,max-page-size=16384 \
  -Wl,-z,common-page-size=16384 \
  -Wl,--build-id=sha1 \
  -Wl,--strip-debug \
  -o "$temporary" \
  "$source_file"
mv "$temporary" "$output"
trap - EXIT

python3 - "$output" <<'PY'
import os, pathlib, struct, subprocess, sys
path = pathlib.Path(sys.argv[1])
environment = dict(os.environ, LC_ALL="C")
data = path.read_bytes()
if data[:4] != b"\x7fELF" or data[4] != 2 or data[5] != 1:
    raise SystemExit("cache helper must be ELF64 little-endian")
if struct.unpack_from("<H", data, 18)[0] != 183:
    raise SystemExit("cache helper must target AArch64")
dynamic = subprocess.run(["readelf", "-dW", str(path)], check=True, text=True, capture_output=True, env=environment).stdout
if "(NEEDED)" in dynamic or "(TEXTREL)" in dynamic:
    raise SystemExit("cache helper must have no dynamic dependencies or text relocations")
relocations = subprocess.run(["readelf", "-rW", str(path)], check=True, text=True, capture_output=True, env=environment).stdout
if "There are no relocations" not in relocations:
    raise SystemExit("cache helper must have no relocations")
symbols = subprocess.run(["readelf", "--dyn-syms", "-W", str(path)], check=True, text=True, capture_output=True, env=environment).stdout
exports = [line for line in symbols.splitlines() if " FUNC " in line and " UND " not in line]
if len(exports) != 1 or "junimogate_clear_cache" not in exports[0]:
    raise SystemExit("cache helper must export only junimogate_clear_cache")
PY

sha256sum "$output"
printf 'Built ARM64 no-libc cache helper: %s\n' "$output"
