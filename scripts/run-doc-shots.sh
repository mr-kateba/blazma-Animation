#!/usr/bin/env bash
# Regenerates the documentation screenshots with the current UI under a virtual display, as a normal user's machine would show them:
# a neutral home folder (DOC_HOME, default /home/maya), exports under ~/Documents/Blazma, the real AI model catalogue and sizes.
# Usage: scripts/run-doc-shots.sh [work-dir]      (PNG files land in <work-dir>/shots; copy the ones the guides use to docs/screenshots)
set -u
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
G="${GODOT:-/opt/godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64}"
OUT="$(realpath -m "${1:-$ROOT/screenshots/doc-shots}")"
SHOTS="$OUT/shots"
DOC_HOME="${DOC_HOME:-/home/maya}"
RES="${DOC_RES:-1536x900}"
mkdir -p "$OUT" "$DOC_HOME/Documents/Blazma" "$DOC_HOME/.local/share" "$DOC_HOME/.config" "$DOC_HOME/.cache"
rm -rf "$SHOTS" "$DOC_HOME/.local/share/"* "$DOC_HOME/.config/"* "$DOC_HOME/.cache/"*; mkdir -p "$SHOTS"
dotnet build "$ROOT/app/godot" >/dev/null 2>&1 || { echo "BUILD FAILED"; exit 1; }
dotnet build "$ROOT/tests/Blazma.FakeKimodoRunner" >/dev/null 2>&1 || { echo "FAKE KIMODO RUNNER BUILD FAILED"; exit 1; }
"$G" --headless --path "$ROOT/app/godot" --import >"$OUT/import.log" 2>&1
run() { # name, args...
  local name="$1"; shift
  env HOME="$DOC_HOME" XDG_DATA_HOME="$DOC_HOME/.local/share" XDG_CONFIG_HOME="$DOC_HOME/.config" XDG_CACHE_HOME="$DOC_HOME/.cache" \
    BLAZMA_DOC_EXPORT_DIR="$DOC_HOME/Documents/Blazma" "$@" \
    xvfb-run -a -s "-screen 0 ${RES}x24" "$G" --path "$ROOT/app/godot" --rendering-driver opengl3 --audio-driver Dummy --resolution "$RES" \
      -- --selftest="$SHOTS" >"$OUT/$name.log" 2>&1
}
run selftest BLAZMA_DUMMY=1
grep -E "SELFTEST RESULT" "$OUT/selftest.log"
# Real catalogue and hardware for the AI models window (overwrites the p10 models shots made with tiny test files above).
run docmodels BLAZMA_ONLY_DOCMODELS=1
grep -E "DOCMODELS|SELFTEST RESULT" "$OUT/docmodels.log"
