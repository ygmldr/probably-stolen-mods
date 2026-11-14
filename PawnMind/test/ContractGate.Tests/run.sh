#!/usr/bin/env bash
# Offline negative tests for the S2 build/contract gate.
#
# These tests only use a fake dotnet, fake assemblies/scripts and the installed
# game DLL through the adapter test process. They never start the game, never
# open a menu, never touch saves, and never execute the dist-packaging section
# of build.sh (PAWNMIND_BUILD_ONLY=1 stops before it, and the build.sh under
# test is a temporary copy whose dist/ is disposable).
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
REAL_DOTNET="${PAWNMIND_REAL_DOTNET:-/mnt/c/Program Files/dotnet/dotnet.exe}"
TMP_PREFIX="pawnmind-contract-gate"
TMP_ROOT="${TMPDIR:-/tmp}"
TMP_ROOT_REAL="$(readlink -f -- "$TMP_ROOT" 2>/dev/null || true)"
if [ -z "$TMP_ROOT_REAL" ] || [ "$TMP_ROOT_REAL" = "/" ] || [ ! -d "$TMP_ROOT_REAL" ]; then
  echo "cleanup guard: refusing unsafe temp root '$TMP_ROOT'" >&2
  exit 2
fi

# Remove only the one temp directory created by this process. The recorded
# canonical path must still match, be a plain directory, sit directly under the
# approved temp root, and carry this script's prefix. Anything else is refused
# and left in place so a human can inspect it.
cleanup_tmp() {
  local current
  if [ -z "${TMP:-}" ]; then
    echo "cleanup guard: TMP is empty; refusing to delete" >&2
    return 1
  fi

  if [ -L "$TMP" ]; then
    echo "cleanup guard: refusing to delete symlink '$TMP'" >&2
    return 1
  fi

  if [ ! -e "$TMP" ]; then
    return 0
  fi

  if [ ! -d "$TMP" ]; then
    echo "cleanup guard: refusing to delete non-directory '$TMP'" >&2
    return 1
  fi

  current="$(readlink -f -- "$TMP" 2>/dev/null || true)"
  if [ -z "${TMP_REAL:-}" ] || [ -z "$current" ] \
    || [ "$current" != "$TMP_REAL" ] || [ "$TMP" != "$TMP_REAL" ]; then
    echo "cleanup guard: refusing to delete '$TMP' (recorded='${TMP_REAL:-}', resolved='${current:-}')" >&2
    return 1
  fi

  if [ -z "${TMP_ROOT_REAL:-}" ] || [ "$TMP_ROOT_REAL" = "/" ]; then
    echo "cleanup guard: refusing unsafe temp root '${TMP_ROOT_REAL:-}'" >&2
    return 1
  fi

  case "$TMP_REAL" in
    "$TMP_ROOT_REAL"/"$TMP_PREFIX".*) ;;
    *)
      echo "cleanup guard: refusing to delete '$TMP' outside '$TMP_ROOT_REAL/${TMP_PREFIX}.*'" >&2
      return 1
      ;;
  esac

  rm -rf -- "${TMP:?}"
}

if ! TMP="$(mktemp -d "$TMP_ROOT_REAL/$TMP_PREFIX.XXXXXX")"; then
  echo "cleanup guard: mktemp failed under '$TMP_ROOT_REAL'" >&2
  exit 2
fi

TMP_REAL="$(readlink -f -- "$TMP" 2>/dev/null || true)"
case "$TMP_REAL" in
  "$TMP_ROOT_REAL"/"$TMP_PREFIX".*) ;;
  *)
    echo "cleanup guard: mktemp returned unexpected path '$TMP' (real '${TMP_REAL:-}'); refusing to delete" >&2
    exit 2
    ;;
esac
if [ "$TMP_REAL" != "$TMP" ] || [ -L "$TMP" ]; then
  echo "cleanup guard: temp path is not canonical '$TMP' (real '${TMP_REAL:-}'); refusing to delete" >&2
  exit 2
fi

trap 'cleanup_tmp || exit 1' EXIT

run_cleanup_self_check() {
  local failures=0
  local saved_tmp="$TMP"
  local saved_real="$TMP_REAL"
  local saved_root="$TMP_ROOT_REAL"
  local probe_root="$TMP/probe-root"
  local log="$TMP/cleanup-probe.log"
  mkdir -p "$probe_root"

  TMP_ROOT_REAL="$probe_root"

  local valid="$probe_root/${TMP_PREFIX}.valid"
  mkdir -p "$valid"
  : >"$valid/sentinel"
  TMP="$valid"
  TMP_REAL="$(readlink -f -- "$TMP")"
  if cleanup_tmp 2>"$log" && [ ! -e "$valid" ]; then
    echo "PASS cleanup guard: valid temp removed"
  else
    echo "FAIL cleanup guard: valid temp removed"
    failures=$((failures + 1))
  fi
  TMP="$saved_tmp"
  TMP_REAL="$saved_real"
  TMP_ROOT_REAL="$saved_root"

  local target="$probe_root/${TMP_PREFIX}.changed"
  local recorded="$probe_root/${TMP_PREFIX}.other"
  mkdir -p "$target" "$recorded"
  : >"$target/sentinel"
  TMP="$target"
  TMP_REAL="$(readlink -f -- "$recorded")"
  if cleanup_tmp 2>"$log" || [ ! -e "$target/sentinel" ]; then
    echo "FAIL cleanup guard: refused changed path"
    failures=$((failures + 1))
  else
    echo "PASS cleanup guard: refused changed path"
  fi
  TMP="$saved_tmp"
  TMP_REAL="$saved_real"
  TMP_ROOT_REAL="$saved_root"

  local outside="$probe_root/${TMP_PREFIX}.outside"
  local link="$probe_root/${TMP_PREFIX}.link"
  mkdir -p "$outside"
  : >"$outside/keep"
  ln -s "$outside" "$link"
  TMP="$link"
  TMP_REAL="$(readlink -f -- "$link")"
  if cleanup_tmp 2>"$log" || [ ! -e "$outside/keep" ]; then
    echo "FAIL cleanup guard: refused symlink"
    failures=$((failures + 1))
  else
    echo "PASS cleanup guard: refused symlink"
  fi
  TMP="$saved_tmp"
  TMP_REAL="$saved_real"
  TMP_ROOT_REAL="$saved_root"

  local outside_root="$saved_tmp/${TMP_PREFIX}.outside-root"
  mkdir -p "$outside_root"
  : >"$outside_root/keep"
  TMP="$outside_root"
  TMP_REAL="$(readlink -f -- "$outside_root")"
  TMP_ROOT_REAL="$probe_root"
  if cleanup_tmp 2>"$log" || [ ! -e "$outside_root/keep" ]; then
    echo "FAIL cleanup guard: refused outside root"
    failures=$((failures + 1))
  else
    echo "PASS cleanup guard: refused outside root"
  fi
  TMP="$saved_tmp"
  TMP_REAL="$saved_real"
  TMP_ROOT_REAL="$saved_root"

  local wrong="$probe_root/not-the-script-prefix"
  mkdir -p "$wrong"
  : >"$wrong/keep"
  TMP="$wrong"
  TMP_REAL="$(readlink -f -- "$wrong")"
  TMP_ROOT_REAL="$probe_root"
  if cleanup_tmp 2>"$log" || [ ! -e "$wrong/keep" ]; then
    echo "FAIL cleanup guard: refused wrong prefix"
    failures=$((failures + 1))
  else
    echo "PASS cleanup guard: refused wrong prefix"
  fi
  TMP="$saved_tmp"
  TMP_REAL="$saved_real"
  TMP_ROOT_REAL="$saved_root"

  local missing_record="$probe_root/${TMP_PREFIX}.missing-record"
  mkdir -p "$missing_record"
  : >"$missing_record/keep"
  TMP="$missing_record"
  TMP_REAL=""
  TMP_ROOT_REAL="$probe_root"
  if cleanup_tmp 2>"$log" || [ ! -e "$missing_record/keep" ]; then
    echo "FAIL cleanup guard: refused missing record"
    failures=$((failures + 1))
  else
    echo "PASS cleanup guard: refused missing record"
  fi
  TMP="$saved_tmp"
  TMP_REAL="$saved_real"
  TMP_ROOT_REAL="$saved_root"

  return "$failures"
}

if [ "${1:-}" = "--self-check-cleanup" ]; then
  run_cleanup_self_check
  exit $?
fi

PASSES=0
FAILURES=0
LAST_STATUS=0
LOG="$TMP/dotnet.log"
OUT="$TMP/out.log"

pass() {
  PASSES=$((PASSES + 1))
  echo "PASS $*"
}

fail() {
  FAILURES=$((FAILURES + 1))
  echo "FAIL $*"
  if [ -f "$OUT" ]; then
    echo "----- output -----"
    sed -n '1,120p' "$OUT"
    echo "------------------"
  fi
}

expect_status() {
  local expected=$1
  local label=$2
  if [ "$LAST_STATUS" -eq "$expected" ]; then
    pass "$label (exit=$LAST_STATUS)"
  else
    fail "$label (expected exit=$expected, got $LAST_STATUS)"
  fi
}

expect_nonzero() {
  local label=$1
  if [ "$LAST_STATUS" -ne 0 ]; then
    pass "$label (exit=$LAST_STATUS)"
  else
    fail "$label (expected non-zero exit, got 0)"
  fi
}

expect_contains() {
  local file=$1
  local needle=$2
  local label=$3
  if grep -Fq -- "$needle" "$file"; then
    pass "$label"
  else
    fail "$label (missing: $needle)"
  fi
}

expect_not_contains() {
  local file=$1
  local needle=$2
  local label=$3
  if grep -Fq -- "$needle" "$file"; then
    fail "$label (unexpected: $needle)"
  else
    pass "$label"
  fi
}

run_cmd() {
  local outfile=$1
  shift
  : >"$LOG"
  if "$@" >"$outfile" 2>&1; then
    LAST_STATUS=0
  else
    LAST_STATUS=$?
  fi
}

to_win_path() {
  local p=$1
  if [[ "$p" =~ ^/mnt/([A-Za-z])/(.*)$ ]]; then
    local drive=${BASH_REMATCH[1]}
    drive=${drive^^}
    local rest=${BASH_REMATCH[2]//\//\\}
    printf '%s:\\%s\n' "$drive" "$rest"
  elif command -v wslpath >/dev/null 2>&1; then
    wslpath -w "$p"
  elif command -v cygpath >/dev/null 2>&1; then
    cygpath -w "$p"
  else
    printf '%s\n' "$p"
  fi
}

# Fake dotnet only records its argv and the PawnMind gate environment.
cat >"$TMP/dotnet" <<'FAKE_DOTNET'
#!/usr/bin/env bash
{
  printf 'ARGS: %s\n' "$*"
  printf 'PAWNMIND_GAME_ASSEMBLY=%s\n' "${PAWNMIND_GAME_ASSEMBLY:-}"
  printf 'PAWNMIND_GAME_MANAGED=%s\n' "${PAWNMIND_GAME_MANAGED:-}"
  printf 'PAWNMIND_REQUIRE_GAME=%s\n' "${PAWNMIND_REQUIRE_GAME:-}"
} >>"${FAKE_DOTNET_LOG:?FAKE_DOTNET_LOG is required}"
exit 0
FAKE_DOTNET
chmod +x "$TMP/dotnet"

MANAGED_PRESENT="$TMP/managed"
mkdir -p "$MANAGED_PRESENT"
: >"$MANAGED_PRESENT/Assembly-CSharp.dll"
MANAGED_MISSING="$TMP/managed-missing"
ALT_ASSEMBLY="$TMP/alt/Assembly-CSharp.dll"
mkdir -p "$(dirname "$ALT_ASSEMBLY")"
: >"$ALT_ASSEMBLY"

BASE_ENV=(DOTNET="$TMP/dotnet" FAKE_DOTNET_LOG="$LOG" PAWNMIND_SCRIPT_TESTS_ACTIVE=1)

echo "== temp cleanup guard =="
run_cmd "$OUT" bash "$0" --self-check-cleanup
expect_status 0 "cleanup guard self-check passes"
expect_contains "$OUT" "PASS cleanup guard: valid temp removed" "cleanup guard removes verified temp"
expect_contains "$OUT" "PASS cleanup guard: refused changed path" "cleanup guard refuses changed path"
expect_contains "$OUT" "PASS cleanup guard: refused symlink" "cleanup guard refuses symlink"
expect_contains "$OUT" "PASS cleanup guard: refused outside root" "cleanup guard refuses outside root"
expect_contains "$OUT" "PASS cleanup guard: refused wrong prefix" "cleanup guard refuses wrong prefix"
expect_contains "$OUT" "PASS cleanup guard: refused missing record" "cleanup guard refuses missing record"

echo "== test.sh default gate =="
run_cmd "$OUT" env -u REQUIRE_GAME -u GAME_MANAGED -u GAME_ASSEMBLY \
  "${BASE_ENV[@]}" GAME_MANAGED="$MANAGED_MISSING" "$ROOT/test.sh"
expect_status 0 "default missing DLL stays compatible"
expect_contains "$OUT" "SKIP adapter tests" "default missing DLL prints compatibility notice"
expect_not_contains "$LOG" "PawnMind.AdapterTests" "default missing DLL does not run adapter"

run_cmd "$OUT" env -u REQUIRE_GAME -u GAME_MANAGED -u GAME_ASSEMBLY \
  "${BASE_ENV[@]}" GAME_MANAGED="$MANAGED_PRESENT" "$ROOT/test.sh"
expect_status 0 "default present DLL runs"
expect_contains "$LOG" "PawnMind.AdapterTests" "default present DLL invokes adapter"
expect_contains "$LOG" "--pawnmind-require-game 0" "default mode passes strict=0"
expect_contains "$LOG" "--pawnmind-game-assembly" "default mode passes assembly path"

echo "== test.sh REQUIRE_GAME=1 gate =="
run_cmd "$OUT" env -u GAME_MANAGED -u GAME_ASSEMBLY \
  "${BASE_ENV[@]}" REQUIRE_GAME=1 GAME_MANAGED="$MANAGED_MISSING" "$ROOT/test.sh"
expect_nonzero "strict missing DLL fails"
expect_contains "$OUT" "FAIL adapter tests" "strict missing DLL reports failure"
expect_contains "$OUT" "REQUIRE_GAME=1" "strict missing DLL explains gate"
expect_not_contains "$OUT" "SKIP adapter tests" "strict missing DLL never silently skips"
expect_not_contains "$LOG" "PawnMind.AdapterTests" "strict missing DLL does not invoke adapter"

run_cmd "$OUT" env -u GAME_MANAGED -u GAME_ASSEMBLY \
  "${BASE_ENV[@]}" REQUIRE_GAME=1 GAME_MANAGED="$MANAGED_PRESENT" "$ROOT/test.sh"
expect_status 0 "strict present DLL runs"
expect_contains "$LOG" "PawnMind.AdapterTests" "strict present DLL invokes adapter"
expect_contains "$LOG" "--pawnmind-require-game 1" "strict mode passes strict=1"

run_cmd "$OUT" env -u GAME_MANAGED -u GAME_ASSEMBLY \
  "${BASE_ENV[@]}" REQUIRE_GAME=maybe GAME_MANAGED="$MANAGED_PRESENT" "$ROOT/test.sh"
expect_nonzero "invalid REQUIRE_GAME value fails"
expect_contains "$OUT" "REQUIRE_GAME" "invalid REQUIRE_GAME value is explained"

echo "== explicit assembly override =="
run_cmd "$OUT" env -u REQUIRE_GAME -u GAME_MANAGED -u GAME_ASSEMBLY \
  "${BASE_ENV[@]}" GAME_ASSEMBLY="$ALT_ASSEMBLY" "$ROOT/test.sh"
expect_status 0 "assembly path override runs"
expect_contains "$LOG" "PawnMind.AdapterTests" "assembly path override invokes adapter"
expect_contains "$LOG" "--pawnmind-game-assembly" "assembly path override passes assembly"
expect_contains "$LOG" "alt" "assembly path override keeps the requested path"

echo "== build.sh portable gate (temporary copy, build-only) =="
BUILDROOT="$TMP/buildroot"
mkdir -p "$BUILDROOT/dist"
cp "$ROOT/build.sh" "$BUILDROOT/build.sh"
cat >"$BUILDROOT/test.sh" <<'FAKE_TEST'
#!/usr/bin/env bash
printf 'FAKE_TEST_SH\n' >>"${FAKE_DOTNET_LOG:?}"
exit 0
FAKE_TEST
chmod +x "$BUILDROOT/test.sh"
: >"$BUILDROOT/dist/sentinel"
run_cmd "$OUT" env -u REQUIRE_GAME -u GAME_ASSEMBLY \
  "${BASE_ENV[@]}" GAME_MANAGED="/mnt/d/Program Files/steam/steamapps/common/Probably Stolen/Probably Stolen_Data/Managed" PAWNMIND_BUILD_ONLY=1 "$BUILDROOT/build.sh"
expect_status 0 "build-only gate exits successfully"
expect_contains "$OUT" "skipping dist packaging" "build-only gate stops before dist"
if [ -f "$BUILDROOT/dist/sentinel" ]; then
  pass "build-only gate leaves dist untouched"
else
  fail "build-only gate leaves dist untouched"
fi
expect_contains "$LOG" "-p:GameManaged=" "build.sh passes GameManaged"
if grep -Eq -- 'GameManaged=[A-Za-z]:\\' "$LOG"; then
  pass "build.sh passes a Windows-style Managed path"
else
  fail "build.sh passes a Windows-style Managed path"
fi

echo "== build.sh dist safety guard =="
make_build_fixture() {
  local root=$1
  mkdir -p "$root/src/bin/Release/netstandard2.1" "$root/dist/PawnMind"
  cp "$ROOT/build.sh" "$root/build.sh"
  cat >"$root/test.sh" <<'FAKE_TEST'
#!/usr/bin/env bash
printf 'FAKE_TEST_SH\n' >>"${FAKE_DOTNET_LOG:?}"
exit 0
FAKE_TEST
  chmod +x "$root/test.sh"
  printf '<manifest/>\n' >"$root/manifest.xml"
  : >"$root/src/PawnMind.csproj"
  : >"$root/src/bin/Release/netstandard2.1/PawnMind.dll"
}

PKGROOT="$TMP/pkgroot"
make_build_fixture "$PKGROOT"
: >"$PKGROOT/dist/old-sentinel"
run_cmd "$OUT" env -u PAWNMIND_BUILD_ONLY -u REQUIRE_GAME -u GAME_ASSEMBLY \
  "${BASE_ENV[@]}" GAME_MANAGED="$MANAGED_PRESENT" "$PKGROOT/build.sh"
expect_status 0 "build.sh packages into temp fixture"
if [ -f "$PKGROOT/dist/PawnMind/manifest.xml" ] && [ -f "$PKGROOT/dist/PawnMind/PawnMind.dll" ]; then
  pass "build.sh writes expected temp package"
else
  fail "build.sh writes expected temp package"
fi
if [ ! -e "$PKGROOT/dist/old-sentinel" ]; then
  pass "build.sh replaces temp dist contents"
else
  fail "build.sh replaces temp dist contents"
fi

SYMROOT="$TMP/symroot"
make_build_fixture "$SYMROOT"
rm -rf "$SYMROOT/dist"
OUTSIDE_DIST="$TMP/outside-dist"
mkdir -p "$OUTSIDE_DIST/PawnMind"
: >"$OUTSIDE_DIST/keep"
ln -s "$OUTSIDE_DIST" "$SYMROOT/dist"
run_cmd "$OUT" env -u PAWNMIND_BUILD_ONLY -u REQUIRE_GAME -u GAME_ASSEMBLY \
  "${BASE_ENV[@]}" GAME_MANAGED="$MANAGED_PRESENT" "$SYMROOT/build.sh"
expect_nonzero "build.sh refuses symlinked dist"
expect_contains "$OUT" "unsafe dist" "build.sh reports unsafe dist"
if [ -f "$OUTSIDE_DIST/keep" ]; then
  pass "symlinked dist target untouched"
else
  fail "symlinked dist target untouched"
fi

BADROOT="$TMP/badroot"
make_build_fixture "$BADROOT"
rm -f "$BADROOT/manifest.xml"
: >"$BADROOT/dist/sentinel"
run_cmd "$OUT" env -u PAWNMIND_BUILD_ONLY -u REQUIRE_GAME -u GAME_ASSEMBLY \
  "${BASE_ENV[@]}" GAME_MANAGED="$MANAGED_PRESENT" "$BADROOT/build.sh"
expect_nonzero "build.sh refuses ROOT without project markers"
expect_contains "$OUT" "unsafe ROOT" "build.sh reports unsafe ROOT"
if [ -f "$BADROOT/dist/sentinel" ]; then
  pass "unsafe ROOT leaves temp dist untouched"
else
  fail "unsafe ROOT leaves temp dist untouched"
fi

echo "== adapter contract harness =="
ADAPTER_PROJECT="$(to_win_path "$ROOT/test/PawnMind.AdapterTests/PawnMind.AdapterTests.csproj")"
run_cmd "$OUT" "$REAL_DOTNET" run --project "$ADAPTER_PROJECT" -c Release -- --contract-self-test
expect_status 0 "adapter self-test passes"
expect_contains "$OUT" "contract self-test OK" "adapter self-test reports OK"
expect_contains "$OUT" "PASS self-test field-type mismatch" "adapter self-test covers field type mismatch"
expect_contains "$OUT" "PASS self-test method-signature mismatch" "adapter self-test covers method signature mismatch"
expect_contains "$OUT" "PASS self-test zero checks" "adapter self-test covers zero checks"

MISSING_ASSEMBLY='Z:\pawnmind-contract-does-not-exist\Assembly-CSharp.dll'
run_cmd "$OUT" "$REAL_DOTNET" run --project "$ADAPTER_PROJECT" -c Release --no-build -- \
  --pawnmind-game-assembly "$MISSING_ASSEMBLY" --pawnmind-require-game 1
expect_nonzero "strict adapter missing DLL fails"
expect_contains "$OUT" "FAIL adapter tests" "strict adapter missing DLL reports failure"
expect_contains "$OUT" "$MISSING_ASSEMBLY" "strict adapter missing DLL names the path"
expect_not_contains "$OUT" "SKIP adapter tests" "strict adapter missing DLL never skips"

run_cmd "$OUT" "$REAL_DOTNET" run --project "$ADAPTER_PROJECT" -c Release --no-build -- \
  --pawnmind-game-assembly "$MISSING_ASSEMBLY"
expect_status 0 "default adapter missing DLL stays compatible"
expect_contains "$OUT" "SKIP adapter tests" "default adapter missing DLL prints compatibility notice"

echo
echo "contract gate tests: passed=$PASSES failed=$FAILURES"
if [ "$FAILURES" -ne 0 ]; then
  exit 1
fi
