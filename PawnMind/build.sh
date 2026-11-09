#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
DOTNET="${DOTNET:-/mnt/c/Program Files/dotnet/dotnet.exe}"
PROJECT="D:/code/game_mods/PawnMind/src/PawnMind.csproj"

"$ROOT/test.sh"

"$DOTNET" build "$PROJECT" -c Release

rm -rf "$ROOT/dist"
mkdir -p "$ROOT/dist/PawnMind"
cp "$ROOT/manifest.xml" "$ROOT/dist/PawnMind/"
cp "$ROOT/src/bin/Release/netstandard2.1/PawnMind.dll" "$ROOT/dist/PawnMind/"

echo
echo "Built dist/PawnMind:"
ls -la "$ROOT/dist/PawnMind/"
