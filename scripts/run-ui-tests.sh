#!/usr/bin/env bash
# Builds the Godot app and runs the UI self-tests under a virtual display.
# Usage: scripts/run-ui-tests.sh [output-dir]      (screenshots land in <output-dir>/shots)
set -u
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
G="${GODOT:-/opt/godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64}"
OUT="$(realpath -m "${1:-$ROOT/screenshots/ui-tests}")"
SHOTS="$OUT/shots"
# Isolated user dir so the tests never touch real settings or recovery data.
export XDG_DATA_HOME="$OUT/xdg-data" XDG_CONFIG_HOME="$OUT/xdg-config" XDG_CACHE_HOME="$OUT/xdg-cache"
rm -rf "$OUT"; mkdir -p "$SHOTS" "$XDG_DATA_HOME" "$XDG_CONFIG_HOME" "$XDG_CACHE_HOME"
FAIL=0

step() { echo; echo "=== $* ==="; }
run_godot() { # name, args...
  local name="$1"; shift
  xvfb-run -a -s "-screen 0 1440x900x24" "$G" --path "$ROOT/app/godot" --rendering-driver opengl3 --audio-driver Dummy "$@" \
    >"$OUT/$name.log" 2>&1
  return $?
}

step "dotnet build"
dotnet build "$ROOT/app/godot" 2>&1 | tail -3 || FAIL=1
dotnet build "$ROOT/app/godot" >/dev/null 2>&1 || { echo "BUILD FAILED"; exit 1; }
# Phase 7: the Kimodo path of the UI tests runs against the fake runner (no AI model involved).
dotnet build "$ROOT/tests/Blazma.FakeKimodoRunner" >/dev/null 2>&1 || { echo "FAKE KIMODO RUNNER BUILD FAILED"; exit 1; }

step "godot import"
"$G" --headless --path "$ROOT/app/godot" --import >"$OUT/import.log" 2>&1
grep -iE "error" "$OUT/import.log" | grep -v "^$" | head -5

step "selftest"
run_godot selftest -- --selftest="$SHOTS"; RC=$?
grep -E "SELFTEST|SCRIPT ERROR|ERROR:" "$OUT/selftest.log" | grep -v "ALSA"
grep -q "SELFTEST RESULT PASS" "$OUT/selftest.log" && [ $RC -eq 0 ] || { echo "selftest FAILED (exit $RC)"; FAIL=1; }

step "phase 2: rendered skeleton vs core model (must stay below 1e-4 m)"
grep "max skeleton error" "$OUT/selftest.log"
grep -E "rotate drag expected|pelvis y =|IK hand target moved|auto-mapping" "$OUT/selftest.log"

step "crashtest phase 1 (process is killed on purpose)"
run_godot crash1 -- --crashtest-phase1="$SHOTS"; RC=$?
grep -E "CRASHTEST|SELFTEST" "$OUT/crash1.log"
[ -f "$SHOTS/phase1.ok" ] || { echo "phase 1 did not reach the crash point"; FAIL=1; }
[ $RC -ne 0 ] || { echo "phase 1 exited cleanly, expected a hard kill"; FAIL=1; }

step "crashtest phase 2 (recovery)"
run_godot crash2 -- --crashtest-phase2="$SHOTS"; RC=$?
grep -E "SELFTEST|recovery rows|SCRIPT ERROR|ERROR:" "$OUT/crash2.log" | grep -v "ALSA"
grep -q "SELFTEST RESULT PASS" "$OUT/crash2.log" && [ $RC -eq 0 ] || { echo "crashtest phase 2 FAILED (exit $RC)"; FAIL=1; }

echo
echo "Phase 7 screenshots:"
ls -1 "$SHOTS"/p7-*.png 2>/dev/null
echo
echo "Phase 2 screenshots (1440x900 unless a dialog):"
ls -1 "$SHOTS"/p2-*.png 2>/dev/null
echo
echo "All screenshots:"
ls -1 "$SHOTS"/*.png 2>/dev/null
if [ $FAIL -eq 0 ]; then echo "ALL UI TESTS PASSED"; else echo "UI TESTS FAILED (logs in $OUT)"; fi
exit $FAIL
