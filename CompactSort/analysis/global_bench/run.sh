#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
DOTNET="${DOTNET:-/mnt/c/Program Files/dotnet/dotnet.exe}"
PROJECT="D:/code/game_mods/CompactSort/analysis/global_bench/GlobalBench.csproj"

"$DOTNET" run --project "$PROJECT" -c Release -- "$@"
