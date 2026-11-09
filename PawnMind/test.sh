#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
DOTNET="${DOTNET:-/mnt/c/Program Files/dotnet/dotnet.exe}"
PURE="D:/code/game_mods/PawnMind/test/PawnMind.Tests/PawnMind.Tests.csproj"
ADAPTER="D:/code/game_mods/PawnMind/test/PawnMind.AdapterTests/PawnMind.AdapterTests.csproj"
MANAGED="/mnt/d/Program Files/steam/steamapps/common/Probably Stolen/Probably Stolen_Data/Managed"

"$DOTNET" run --project "$PURE" -c Release -- "$@"

if [ -f "$MANAGED/Assembly-CSharp.dll" ]; then
  "$DOTNET" run --project "$ADAPTER" -c Release -- "$@"
else
  echo
  echo "SKIP adapter tests: game Assembly-CSharp.dll not found at:"
  echo "  $MANAGED"
fi
