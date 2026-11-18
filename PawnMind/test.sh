#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
DOTNET="${DOTNET:-/mnt/c/Program Files/dotnet/dotnet.exe}"
DEFAULT_GAME_MANAGED="/mnt/d/Program Files/steam/steamapps/common/Probably Stolen/Probably Stolen_Data/Managed"

# Convert a WSL/Linux path to the Windows path expected by dotnet.exe. A path
# that is already Windows-shaped is passed through unchanged.
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

# Accept either a WSL path (/mnt/d/...) or a Windows path (D:\...) when
# checking local existence. The adapter itself always gets a Windows path.
to_unix_path() {
  local path="$1"
  if [[ "$path" =~ ^([A-Za-z]):[\\/](.*)$ ]]; then
    local drive="${BASH_REMATCH[1],,}"
    local rest="${BASH_REMATCH[2]//\\//}"
    if command -v cygpath >/dev/null 2>&1; then
      cygpath -u "$path"
    elif [ -d "/mnt/$drive" ]; then
      printf '/mnt/%s/%s\n' "$drive" "$rest"
    else
      printf '%s\n' "$path"
    fi
  else
    printf '%s\n' "$path"
  fi
}

PURE="$(to_win_path "$ROOT/test/PawnMind.Tests/PawnMind.Tests.csproj")"
ADAPTER="$(to_win_path "$ROOT/test/PawnMind.AdapterTests/PawnMind.AdapterTests.csproj")"

REQUIRE_GAME_RAW="${REQUIRE_GAME:-0}"
case "$REQUIRE_GAME_RAW" in
  1|true|TRUE|yes|YES|on|ON) REQUIRE_GAME_STRICT=1 ;;
  0|false|FALSE|no|NO|off|OFF|"") REQUIRE_GAME_STRICT=0 ;;
  *)
    echo "FAIL: REQUIRE_GAME must be 0 or 1 (got: $REQUIRE_GAME_RAW)" >&2
    exit 2
    ;;
esac

if [ -n "${GAME_ASSEMBLY:-}" ]; then
  ASSEMBLY_PATH="$(to_unix_path "$GAME_ASSEMBLY")"
  MANAGED_PATH="$(dirname "$ASSEMBLY_PATH")"
else
  MANAGED_PATH="$(to_unix_path "${GAME_MANAGED:-$DEFAULT_GAME_MANAGED}")"
  ASSEMBLY_PATH="$MANAGED_PATH/Assembly-CSharp.dll"
fi

"$DOTNET" run --project "$PURE" -c Release -- "$@"

# Script-level negative tests are only part of the full run. They invoke this
# script with a fake dotnet, so the guard prevents recursion.
if [ "$#" -eq 0 ] && [ "${PAWNMIND_SCRIPT_TESTS_ACTIVE:-0}" != "1" ] \
  && [ -x "$ROOT/test/ContractGate.Tests/run.sh" ]; then
  echo
  echo "== contract gate tests =="
  PAWNMIND_SCRIPT_TESTS_ACTIVE=1 PAWNMIND_REAL_DOTNET="$DOTNET" \
    "$ROOT/test/ContractGate.Tests/run.sh"
fi

if [ -f "$ASSEMBLY_PATH" ]; then
  export PAWNMIND_GAME_ASSEMBLY="$(to_win_path "$ASSEMBLY_PATH")"
  export PAWNMIND_GAME_MANAGED="$(to_win_path "$MANAGED_PATH")"
  export PAWNMIND_REQUIRE_GAME="$REQUIRE_GAME_STRICT"
  echo
  echo "adapter game assembly: $ASSEMBLY_PATH"
  echo "adapter strict mode: REQUIRE_GAME=$REQUIRE_GAME_STRICT"
  "$DOTNET" run --project "$ADAPTER" -c Release -- \
    --pawnmind-game-assembly "$(to_win_path "$ASSEMBLY_PATH")" \
    --pawnmind-managed "$(to_win_path "$MANAGED_PATH")" \
    --pawnmind-require-game "$REQUIRE_GAME_STRICT" \
    "$@"
else
  if [ "$REQUIRE_GAME_STRICT" = "1" ]; then
    echo
    echo "FAIL adapter tests: game Assembly-CSharp.dll not found at:" >&2
    echo "  $ASSEMBLY_PATH" >&2
    echo "REQUIRE_GAME=1: strict mode forbids silently skipping game contracts." >&2
    exit 2
  fi

  echo
  echo "SKIP adapter tests: game Assembly-CSharp.dll not found at:"
  echo "  $ASSEMBLY_PATH"
fi
