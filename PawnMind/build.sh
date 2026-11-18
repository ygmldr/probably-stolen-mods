#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
DOTNET="${DOTNET:-/mnt/c/Program Files/dotnet/dotnet.exe}"
DEFAULT_GAME_MANAGED="/mnt/d/Program Files/steam/steamapps/common/Probably Stolen/Probably Stolen_Data/Managed"

to_win_path() {
  local path="$1"
  if [[ "$path" =~ ^[A-Za-z]:[\\/] ]]; then
    printf '%s\n' "$path"
  elif [[ "$path" =~ ^/mnt/([A-Za-z])/(.*)$ ]]; then
    local drive="${BASH_REMATCH[1]^^}"
    local rest="${BASH_REMATCH[2]//\//\\}"
    printf '%s:\\%s\n' "$drive" "$rest"
  elif command -v wslpath >/dev/null 2>&1; then
    wslpath -w "$path"
  elif command -v cygpath >/dev/null 2>&1; then
    cygpath -w "$path"
  else
    printf '%s\n' "$path"
  fi
}

PROJECT="$(to_win_path "$ROOT/src/PawnMind.csproj")"

if [ -n "${GAME_ASSEMBLY:-}" ]; then
  if [[ "$GAME_ASSEMBLY" == *\\* && "$GAME_ASSEMBLY" != */* ]]; then
    GAME_MANAGED="${GAME_ASSEMBLY%\\*}"
  else
    GAME_MANAGED="$(dirname "$GAME_ASSEMBLY")"
  fi
else
  GAME_MANAGED="${GAME_MANAGED:-$DEFAULT_GAME_MANAGED}"
fi

GAME_MANAGED_WIN="$(to_win_path "$GAME_MANAGED")"
export GAME_MANAGED

"$ROOT/test.sh"

"$DOTNET" build "$PROJECT" -c Release -p:GameManaged="$GAME_MANAGED_WIN"

# Offline build/contract gate: stop before touching dist/. This is what S2 and
# CI use to verify the portable build without packaging or installing anything.
if [ "${PAWNMIND_BUILD_ONLY:-0}" = "1" ]; then
  echo
  echo "PAWNMIND_BUILD_ONLY=1: build gate complete; skipping dist packaging."
  exit 0
fi

# Fail closed before the recursive delete. The canonical ROOT must be the
# actual script directory with the expected project markers, and dist must be a
# plain child directory (never a symlink that could redirect a delete/write).
assert_safe_dist() {
  local root_real
  local dist
  local dist_real

  if [ -z "${ROOT:-}" ] || [ "${ROOT#/}" = "${ROOT}" ]; then
    echo "unsafe ROOT: '${ROOT:-}' is not an absolute path" >&2
    return 1
  fi

  root_real="$(readlink -f -- "$ROOT" 2>/dev/null || true)"
  if [ -z "$root_real" ] || [ "$root_real" = "/" ] || [ "$root_real" != "$ROOT" ]; then
    echo "unsafe ROOT: '$ROOT' resolves to '${root_real:-}' (expected the script directory)" >&2
    return 1
  fi

  if [ ! -f "$root_real/manifest.xml" ] \
    || [ ! -f "$root_real/src/PawnMind.csproj" ] \
    || [ ! -f "$root_real/test.sh" ]; then
    echo "unsafe ROOT: '$root_real' lacks manifest.xml/src/PawnMind.csproj/test.sh" >&2
    return 1
  fi

  dist="$root_real/dist"
  if [ -L "$dist" ]; then
    echo "unsafe dist: '$dist' is a symlink; refusing to delete/package" >&2
    return 1
  fi

  if [ -e "$dist" ]; then
    if [ ! -d "$dist" ]; then
      echo "unsafe dist: '$dist' is not a directory" >&2
      return 1
    fi
    dist_real="$(readlink -f -- "$dist" 2>/dev/null || true)"
    if [ -z "$dist_real" ] || [ "$dist_real" != "$dist" ]; then
      echo "unsafe dist: '$dist' resolves to '${dist_real:-}'" >&2
      return 1
    fi
  fi

  DIST="$dist"
}

if ! assert_safe_dist; then
  exit 1
fi

rm -rf -- "${DIST:?}"
mkdir -p "$DIST/PawnMind"
cp "$ROOT/manifest.xml" "$DIST/PawnMind/"
cp "$ROOT/src/bin/Release/netstandard2.1/PawnMind.dll" "$DIST/PawnMind/"

echo
echo "Built dist/PawnMind:"
ls -la "$DIST/PawnMind/"
