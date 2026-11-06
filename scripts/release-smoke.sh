#!/usr/bin/env bash
# Release smoke tests, runnable on Linux with no Windows machine.
# Usage: scripts/release-smoke.sh <version> [--full]
#   Linux artifacts are really launched (headless) and the agent tools are run.
#   Windows artifacts are verified structurally (PE headers, DLLs, zip integrity, installer type).
#   --full also runs the app's complete UI self-test (xvfb, ~4 minutes) against the exported Linux build.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
V="${1:?usage: $0 <version> [--full]}"; FULL=0; [ "${2:-}" = "--full" ] && FULL=1
D="$ROOT/build/release/$V"
T="$(mktemp -d)"; trap 'rm -rf "$T"' EXIT
FAIL=0
ok()  { echo "  PASS  $*"; }
bad() { echo "  FAIL  $*"; FAIL=1; }
chk() { local name="$1"; shift; if "$@" >/dev/null 2>&1; then ok "$name"; else bad "$name"; fi; }
isa() { file -b "$2" | grep -q "$1"; }
[ -d "$D" ] || { echo "no $D — run scripts/build-release.sh $V first"; exit 1; }

echo "== checksums"
chk "SHA256SUMS.txt matches every artifact" bash -c "cd '$D' && sha256sum -c SHA256SUMS.txt"
for f in "BlazmaAnimation-$V-win64.zip" "BlazmaAnimation-$V-setup.exe" "BlazmaAnimation-$V-linux-x64.tar.gz" "BlazmaAnimation-$V-x86_64.AppImage" "RELEASE_NOTES-$V.md"; do
  [ -s "$D/$f" ] && ok "present: $f" || bad "missing: $f"; done

echo "== Windows zip (structural)"
Z="$D/BlazmaAnimation-$V-win64.zip"; P="BlazmaAnimation-$V-win64"
chk "zip integrity (unzip -t)" unzip -tq "$Z"
unzip -q "$Z" -d "$T/win"; W="$T/win/$P"
isa "PE32+ executable.*x86-64" "$W/BlazmaAnimation.exe" && ok "BlazmaAnimation.exe is a PE32+ x86-64" || bad "BlazmaAnimation.exe not PE32+ x86-64"
for t in blazma-mcp.exe blazma-anim.exe; do isa "PE32+ executable.*x86-64" "$W/tools/$t" && ok "tools/$t is PE32+ x86-64" || bad "tools/$t"; done
for f in BlazmaAnimation.pck portable.txt VERSION.txt THIRD_PARTY_NOTICES.md GODOT_LICENSE.txt OFL-IBM-Plex.txt docs/AGENT_SETUP_AR.md docs/AI_INTEGRATION.md \
         data_Blazma.UI_windows_x86_64/Blazma.UI.dll data_Blazma.UI_windows_x86_64/Blazma.Core.dll data_Blazma.UI_windows_x86_64/GodotSharp.dll \
         data_Blazma.UI_windows_x86_64/coreclr.dll data_Blazma.UI_windows_x86_64/hostfxr.dll data_Blazma.UI_windows_x86_64/System.Private.CoreLib.dll; do
  [ -s "$W/$f" ] && ok "contains $f" || bad "missing $f"; done
for d in Blazma.UI Blazma.Core GodotSharp; do isa "Mono/.Net assembly" "$W/data_Blazma.UI_windows_x86_64/$d.dll" && ok "$d.dll is a .NET assembly" || bad "$d.dll not a .NET assembly"; done
[ -z "$(find "$W" -name '*.pdb')" ] && ok "no .pdb files shipped" || bad "pdb files in zip"

echo "== Windows installer (structural)"
S="$D/BlazmaAnimation-$V-setup.exe"
isa "PE32 executable" "$S" && ok "setup.exe is a PE executable" || bad "setup.exe not PE"
file -b "$S" | grep -qi "Nullsoft" && ok "setup.exe is an NSIS installer" || bad "setup.exe is not NSIS"
chk "setup.exe is larger than 50 MB (payload present)" test "$(stat -c %s "$S")" -gt 52428800
if command -v 7z >/dev/null; then
  7z l "$S" 2>/dev/null | grep -q "BlazmaAnimation.exe" && ok "installer payload lists BlazmaAnimation.exe" || bad "installer payload lacks BlazmaAnimation.exe"
fi

echo "== Linux tar.gz (really launched)"
export XDG_DATA_HOME="$T/xdg-data" XDG_CONFIG_HOME="$T/xdg-config" XDG_CACHE_HOME="$T/xdg-cache"; mkdir -p "$XDG_DATA_HOME"
chk "tar.gz integrity" tar tzf "$D/BlazmaAnimation-$V-linux-x64.tar.gz"
tar xzf "$D/BlazmaAnimation-$V-linux-x64.tar.gz" -C "$T"; L="$T/BlazmaAnimation-$V-linux-x64"
isa "ELF 64-bit.*x86-64" "$L/BlazmaAnimation.x86_64" && ok "app is ELF x86-64" || bad "app not ELF"
timeout 90 "$L/BlazmaAnimation.x86_64" --headless --quit-after 60 >"$T/launch.log" 2>&1; RC=$?
[ $RC -eq 0 ] && ok "headless launch exits cleanly (rc 0)" || { bad "headless launch rc=$RC"; tail -5 "$T/launch.log"; }
grep -qE "SCRIPT ERROR|Failed to create an autoload|not compiling" "$T/launch.log" && bad "script errors at startup" || ok "no script/autoload errors at startup"
# The tarball ships portable.txt, so the app keeps its data in userdata/ next to the executable.
[ -d "$L/userdata/recovery" ] && ok "C# app code ran in portable mode (userdata/ next to the app)" || bad "app code did not run (no userdata/recovery)"
[ ! -e "$XDG_DATA_HOME/BlazmaAnimation/recovery" ] && ok "portable mode left the user data folder untouched" || bad "portable mode still wrote to the user data folder"
# Without the marker the app uses the normal per-user folder.
rm "$L/portable.txt"; rm -rf "$L/userdata"
timeout 90 "$L/BlazmaAnimation.x86_64" --headless --quit-after 60 >"$T/launch2.log" 2>&1
[ -d "$XDG_DATA_HOME/BlazmaAnimation/recovery" ] && [ ! -e "$L/userdata" ] && ok "without portable.txt the app uses the user data folder" || bad "non-portable launch did not use the user data folder"
"$L/tools/blazma-anim" commands >"$T/cmds.txt" 2>&1 && [ "$(wc -l <"$T/cmds.txt")" -gt 50 ] && ok "blazma-anim commands ($(wc -l <"$T/cmds.txt") commands)" || bad "blazma-anim commands"
"$L/tools/blazma-mcp" --help 2>&1 | grep -q "MCP server" && ok "blazma-mcp --help" || bad "blazma-mcp --help"
# MCP handshake: initialize must answer on stdio
INIT='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}'
(echo "$INIT"; sleep 4) | timeout 20 "$L/tools/blazma-mcp" --headless 2>/dev/null | head -1 | grep -q '"serverInfo"' && ok "blazma-mcp answers an MCP initialize" || bad "blazma-mcp initialize"

echo "== AppImage (really launched)"
A="$D/BlazmaAnimation-$V-x86_64.AppImage"
isa "ELF 64-bit" "$A" && ok "AppImage is ELF" || bad "AppImage not ELF"
APPIMAGE_EXTRACT_AND_RUN=1 timeout 90 "$A" --headless --quit-after 60 >"$T/ai.log" 2>&1; RC=$?
[ $RC -eq 0 ] && ok "AppImage headless launch exits cleanly" || { bad "AppImage launch rc=$RC"; tail -5 "$T/ai.log"; }

if [ $FULL = 1 ]; then
  echo "== full UI self-test on the exported Linux build (xvfb, several minutes)"
  # The self-test reads ../../samples and ../../tests relative to the exe folder: mimic the repo layout.
  mkdir -p "$T/full/x"; ln -s "$ROOT/samples" "$T/full/samples"; ln -s "$ROOT/tests" "$T/full/tests"; cp -r "$L" "$T/full/x/"
  timeout 600 xvfb-run -a -s "-screen 0 1440x900x24" "$T/full/x/$(basename "$L")/BlazmaAnimation.x86_64" --rendering-driver opengl3 --audio-driver Dummy -- --selftest="$T/shots" >"$T/selftest.log" 2>&1
  grep -q "SELFTEST RESULT PASS" "$T/selftest.log" && ok "exported build passes the UI self-test ($(grep -c 'SELFTEST PASS' "$T/selftest.log") checks)" || { bad "UI self-test"; grep "SELFTEST FAIL" "$T/selftest.log" | head; }
fi

echo
[ $FAIL -eq 0 ] && echo "RELEASE SMOKE PASSED" || echo "RELEASE SMOKE FAILED"
exit $FAIL
