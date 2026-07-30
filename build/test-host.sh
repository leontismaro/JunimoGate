#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="${CONFIGURATION:-Debug}"
dotnet_command="dotnet"

if [[ -x "$root/.toolchains/dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$root/.toolchains/dotnet"
  export DOTNET_CLI_HOME="$root/.toolchains/dotnet-home"
  export DOTNET_MULTILEVEL_LOOKUP=0
  export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
  export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
  export NUGET_PACKAGES="$root/.toolchains/nuget-packages"
  export NUGET_HTTP_CACHE_PATH="$root/.toolchains/nuget-http-cache"
  export NUGET_XMLDOC_MODE=skip
  dotnet_command="$DOTNET_ROOT/dotnet"
fi

if [[ -x "$root/.toolchains/dotnet/dotnet" ]]; then
  "$root/build/build-harmony-android.sh"
else
  expected_package="$root/artifacts/nuget/Lib.Harmony.2.4.2-junimogate.11.nupkg"
  [[ -f "$expected_package" ]] || {
    printf 'Missing patched Harmony package: %s\n' "$expected_package" >&2
    printf 'Bootstrap the project-local Android toolchain, then run build/build-harmony-android.sh.\n' >&2
    exit 1
  }
fi

"$dotnet_command" build "$root/JunimoGate.Host.slnf" --configuration "$configuration" -m:1

for project in \
  JunimoGate.Core.Tests \
  JunimoGate.Extraction.Tests \
  JunimoGate.Rewriter.Tests \
  JunimoGate.Mods.Tests \
  JunimoGate.RuntimeProbe.Tests
do
  "$dotnet_command" run \
    --project "$root/tests/$project/$project.csproj" \
    --configuration "$configuration" \
    --no-build
done
