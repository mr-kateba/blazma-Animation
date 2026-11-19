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

echo "== release notes"
N="$D/RELEASE_NOTES-$V.md"
grep -q "^# Blazma Animation $V\$" "$N" && ok "release notes title" || bad "release notes title"
grep -q "## التثبيت" "$N" && ok "release notes have the install block" || bad "release notes lack the install block"
if grep -q "^## ${V%%-*} " "$ROOT/CHANGELOG.md" || grep -q "^## $V " "$ROOT/CHANGELOG.md"; then
  grep -q "^## What's new in $V\$" "$N" && ok "release notes carry this version's CHANGELOG section" || bad "release notes lack the CHANGELOG section"
fi
grep -qE "PHASE|بانتظار تجربتك" "$N" && bad "release notes still quote the old phase reports" || ok "release notes do not quote the phase reports"

echo "== Windows zip (structural)"
Z="$D/BlazmaAnimation-$V-win64.zip"; P="BlazmaAnimation-$V-win64"
chk "zip integrity (unzip -t)" unzip -tq "$Z"
unzip -q "$Z" -d "$T/win"; W="$T/win/$P"
isa "PE32+ executable.*x86-64" "$W/BlazmaAnimation.exe" && ok "BlazmaAnimation.exe is a PE32+ x86-64" || bad "BlazmaAnimation.exe not PE32+ x86-64"
for t in blazma-mcp.exe blazma-anim.exe; do isa "PE32+ executable.*x86-64" "$W/tools/$t" && ok "tools/$t is PE32+ x86-64" || bad "tools/$t"; done
for f in BlazmaAnimation.pck portable.txt VERSION.txt LICENSE.txt THIRD_PARTY_NOTICES.md legal/GODOT_LICENSE.txt legal/GODOT_COPYRIGHT.txt legal/OFL-IBM-Plex.txt \
         legal/APACHE-2.0.txt legal/DOTNET_LICENSE.txt legal/DOTNET8_THIRD_PARTY_NOTICES.txt legal/DOTNET10_THIRD_PARTY_NOTICES.txt legal/MCP_CSHARP_SDK_LICENSE.txt \
         legal/NVIDIA_KIMODO_LICENSE.txt legal/KIMODO_CPP_NOTICE.txt legal/GGML_LICENSE.txt legal/NLOHMANN_JSON_LICENSE.txt docs/AGENT_SETUP_AR.md docs/AI_INTEGRATION.md \
         data_Blazma.UI_windows_x86_64/Blazma.UI.dll data_Blazma.UI_windows_x86_64/Blazma.Core.dll data_Blazma.UI_windows_x86_64/GodotSharp.dll \
         data_Blazma.UI_windows_x86_64/coreclr.dll data_Blazma.UI_windows_x86_64/hostfxr.dll data_Blazma.UI_windows_x86_64/System.Private.CoreLib.dll; do
  [ -s "$W/$f" ] && ok "contains $f" || bad "missing $f"; done
for d in Blazma.UI Blazma.Core GodotSharp; do isa "Mono/.Net assembly" "$W/data_Blazma.UI_windows_x86_64/$d.dll" && ok "$d.dll is a .NET assembly" || bad "$d.dll not a .NET assembly"; done
[ -z "$(find "$W" -name '*.pdb')" ] && ok "no .pdb files shipped" || bad "pdb files in zip"
# A runner was staged for this release (its own zip is published): then the app zip must carry it in tools/kimodo.
if [ -f "$D/kimodo-runner-$V-win64.zip" ]; then
  [ -s "$W/tools/kimodo/kimodo-runner.exe" ] && ok "tools/kimodo/kimodo-runner.exe present (runner staged)" || bad "runner staged but tools/kimodo/kimodo-runner.exe missing"
  # Run-time GGML backends next to the runner (DECISIONS D44): CPU variants and Vulkan always, CUDA with its runtime DLLs.
  for f in kimodo.dll ggml.dll ggml-base.dll ggml-cpu-x64.dll ggml-cpu-haswell.dll ggml-vulkan.dll; do
    [ -s "$W/tools/kimodo/$f" ] && ok "tools/kimodo/$f present" || bad "tools/kimodo/$f missing (multi-backend runner)"
  done
  # The Microsoft Visual C++ runtime next to the runner (DECISIONS D49): no vc_redist, no administrator rights.
  for f in msvcp140.dll msvcp140_1.dll msvcp140_2.dll msvcp140_atomic_wait.dll msvcp140_codecvt_ids.dll vcruntime140.dll vcruntime140_1.dll concrt140.dll vcomp140.dll; do
    isa "PE32+ executable.*x86-64" "$W/tools/kimodo/$f" && ok "tools/kimodo/$f present (app-local VC++ runtime)" || bad "tools/kimodo/$f missing (app-local VC++ runtime)"
  done
  [ -s "$W/tools/kimodo/SHA256SUMS-vcruntime.txt" ] && (cd "$W/tools/kimodo" && sha256sum -c --quiet SHA256SUMS-vcruntime.txt) >/dev/null 2>&1 \
    && ok "tools/kimodo/SHA256SUMS-vcruntime.txt verifies" || bad "tools/kimodo/SHA256SUMS-vcruntime.txt missing or wrong"
  [ -s "$W/legal/MSVC_REDIST_NOTICE.txt" ] && grep -q "app-local" "$W/legal/MSVC_REDIST_NOTICE.txt" && ok "legal/MSVC_REDIST_NOTICE.txt (app-local runtime terms)" || bad "legal/MSVC_REDIST_NOTICE.txt missing or outdated"
  python3 -I "$ROOT/scripts/pe-deps.py" check "$W/tools/kimodo" >"$T/pe-deps.txt" 2>&1 && ok "every DLL in tools/kimodo resolves its imports there or in Windows ($(grep -o 'runtime in the folder: .*' "$T/pe-deps.txt"))" \
    || { bad "tools/kimodo has unresolved DLL imports"; grep MISSING "$T/pe-deps.txt" | head; }
  unzip -l "$D/kimodo-runner-$V-win64.zip" | grep -qi "vcruntime140.dll" && ok "kimodo-runner zip carries the VC++ runtime" || bad "kimodo-runner zip lacks the VC++ runtime"
  [ ! -e "$W/README-VC_REDIST.txt" ] && ok "no README-VC_REDIST.txt (nothing to install by hand)" || bad "README-VC_REDIST.txt still in the zip"
  # The CUDA backend is a separate, optional download (DECISIONS D47): never in the app zip.
  for f in ggml-cuda.dll cudart64_12.dll cublas64_12.dll cublasLt64_12.dll; do
    [ -e "$W/tools/kimodo/$f" ] && bad "tools/kimodo/$f is in the app zip (CUDA must be the separate asset)" || ok "no $f in the app zip"
  done
  [ -f "$D/kimodo-runner-$V-win64.zip" ] && unzip -l "$D/kimodo-runner-$V-win64.zip" | grep -qiE 'ggml-cuda|cublas|cudart' && bad "kimodo-runner zip still carries CUDA files" || ok "kimodo-runner zip has no CUDA files"
  [ -s "$W/legal/NVIDIA_CUDA_NOTICE.txt" ] && [ -s "$W/legal/SPIRV_HEADERS_LICENSE.txt" ] && ok "CUDA and SPIRV-Headers notices in legal/" || bad "legal/NVIDIA_CUDA_NOTICE.txt or SPIRV_HEADERS_LICENSE.txt missing"
else echo "  NOTE  no Windows kimodo-runner staged for $V (tools/kimodo not expected)"; fi

echo "== optional CUDA backend asset (DECISIONS D47)"
C="$D/blazma-cuda-backend-$V-win-x64.zip"; J="$W/tools/kimodo/cuda-backend.json"
if [ -f "$C" ]; then
  chk "CUDA zip integrity (unzip -t)" unzip -tq "$C"
  [ -s "$J" ] && ok "tools/kimodo/cuda-backend.json present" || bad "CUDA asset built but tools/kimodo/cuda-backend.json missing"
  unzip -q "$C" -d "$T/cuda"; CD="$T/cuda/blazma-cuda-backend-$V-win-x64"
  for f in ggml-cuda.dll cudart64_12.dll cublas64_12.dll cublasLt64_12.dll NVIDIA_CUDA_NOTICE.txt; do
    [ -s "$CD/$f" ] && ok "CUDA zip has $f" || bad "CUDA zip lacks $f"; done
  isa "PE32+ executable.*x86-64" "$CD/ggml-cuda.dll" && ok "ggml-cuda.dll is PE32+ x86-64" || bad "ggml-cuda.dll not PE32+ x86-64"
  chk "CUDA zip SHA256SUMS-cuda-backend.txt verifies" bash -c "cd '$CD' && sha256sum -c --quiet SHA256SUMS-cuda-backend.txt"
  # What the app checks the download against: the zip's size and SHA-256 and every file's, as shipped in cuda-backend.json.
  if python3 -I - "$J" "$C" "$CD" "$V" <<'PY' >"$T/cuda-json.txt" 2>&1
import hashlib, json, os, sys
j, z, d, v = sys.argv[1:]
m = json.load(open(j))
sha = lambda p: hashlib.sha256(open(p, "rb").read()).hexdigest()
assert m["format"] == 1 and m["version"] == v, "format/version"
assert m["asset"] == os.path.basename(z), "asset name"
assert m["url"].startswith("https://github.com/") and m["url"].endswith("/releases/download/v%s/%s" % (v, m["asset"])), "url"
assert m["size"] == os.path.getsize(z) and m["sha256"] == sha(z), "zip size/sha256"
assert m["library"] == "ggml-cuda.dll" and any(f["name"] == m["library"] for f in m["files"]), "library"
for f in m["files"]:
    p = os.path.join(d, f["name"])
    assert os.path.getsize(p) == f["size"] and sha(p) == f["sha256"], f["name"]
print("ok")
PY
  then ok "cuda-backend.json matches the CUDA zip (size, SHA-256, every file)"; else bad "cuda-backend.json does not match the CUDA zip: $(cat "$T/cuda-json.txt" | tail -1)"; fi
  grep -q "blazma-cuda-backend-$V-win-x64.zip" "$D/SHA256SUMS.txt" && ok "CUDA asset listed in SHA256SUMS.txt" || bad "CUDA asset not in SHA256SUMS.txt"
  grep -q "$(sha256sum "$C" | cut -d' ' -f1)" "$D/RELEASE_NOTES-$V.md" && ok "release notes give the CUDA asset's SHA-256" || bad "release notes lack the CUDA SHA-256"
else
  [ -e "$J" ] && bad "cuda-backend.json shipped but no CUDA asset was built" || echo "  NOTE  no CUDA asset for $V (no ggml-cuda.dll in the runner build): NVIDIA cards use Vulkan"
fi

echo "== Windows installer (structural)"
S="$D/BlazmaAnimation-$V-setup.exe"
isa "PE32 executable" "$S" && ok "setup.exe is a PE executable" || bad "setup.exe not PE"
file -b "$S" | grep -qi "Nullsoft" && ok "setup.exe is an NSIS installer" || bad "setup.exe is not NSIS"
chk "setup.exe is larger than 50 MB (payload present)" test "$(stat -c %s "$S")" -gt 52428800
# Without the CUDA backend (D47) the installer stays small (~150 MB); 250 MB means CUDA crept back in.
chk "setup.exe is smaller than 250 MB ($(( $(stat -c %s "$S") / 1048576 )) MB, CUDA not bundled)" test "$(stat -c %s "$S")" -lt 262144000
if command -v 7z >/dev/null; then
  7z l "$S" >"$T/setup-list.txt" 2>/dev/null
  grep -q "BlazmaAnimation.exe" "$T/setup-list.txt" && ok "installer payload lists BlazmaAnimation.exe" || bad "installer payload lacks BlazmaAnimation.exe"
  grep -q "portable.txt" "$T/setup-list.txt" && bad "installer payload contains portable.txt (installed copy would run portable)" || ok "installer payload has no portable.txt"
  grep -q "LICENSE.txt" "$T/setup-list.txt" && ok "installer payload lists LICENSE.txt" || bad "installer payload lacks LICENSE.txt"
  if [ -f "$D/kimodo-runner-$V-win64.zip" ]; then
    grep -q "kimodo-runner.exe" "$T/setup-list.txt" && ok "installer payload has tools/kimodo/kimodo-runner.exe" || bad "runner staged but missing from the installer"
    grep -q "ggml-vulkan.dll" "$T/setup-list.txt" && grep -q "ggml-cpu-x64.dll" "$T/setup-list.txt" && ok "installer payload has the GGML backends" || bad "installer payload lacks ggml-vulkan.dll / ggml-cpu-*.dll"
    grep -qiE "ggml-cuda|cublas|cudart" "$T/setup-list.txt" && bad "installer payload contains CUDA files (must be the separate asset)" || ok "installer payload has no CUDA files"
    grep -q "cuda-backend.json" "$T/setup-list.txt" || [ ! -f "$C" ] && ok "installer payload has tools/kimodo/cuda-backend.json (when the CUDA asset exists)" || bad "installer lacks cuda-backend.json"
    grep -qi "vcruntime140_1.dll" "$T/setup-list.txt" && grep -qi "msvcp140.dll" "$T/setup-list.txt" && grep -qi "vcomp140.dll" "$T/setup-list.txt" \
      && ok "installer payload has the app-local VC++ runtime (msvcp140, vcruntime140_1, vcomp140)" || bad "installer payload lacks the VC++ runtime DLLs"
    grep -qi "vc_redist\.x64\.exe" "$T/setup-list.txt" && bad "installer still carries vc_redist.x64.exe (D49: app-local runtime instead)" || ok "installer has no vc_redist.x64.exe (no administrator prompt)"
    unzip -l "$D/BlazmaAnimation-$V-win64.zip" | grep -qi "vc_redist\.x64\.exe" && bad "vc_redist.x64.exe must not be in the portable zip" || ok "portable zip has no vc_redist.x64.exe"
  fi
else
  echo "  SKIP  7z not installed: installer payload not listed (portable.txt absence and runner not checked; apt-get install -y p7zip-full)"
fi

echo "== Linux tar.gz (really launched)"
export XDG_DATA_HOME="$T/xdg-data" XDG_CONFIG_HOME="$T/xdg-config" XDG_CACHE_HOME="$T/xdg-cache"; mkdir -p "$XDG_DATA_HOME"
chk "tar.gz integrity" tar tzf "$D/BlazmaAnimation-$V-linux-x64.tar.gz"
tar xzf "$D/BlazmaAnimation-$V-linux-x64.tar.gz" -C "$T"; L="$T/BlazmaAnimation-$V-linux-x64"
isa "ELF 64-bit.*x86-64" "$L/BlazmaAnimation.x86_64" && ok "app is ELF x86-64" || bad "app not ELF"
grep -q '^Exec=BlazmaAnimation.x86_64 ' "$L/blazma-animation.desktop" && ok ".desktop Exec names the real binary" || bad ".desktop Exec does not name BlazmaAnimation.x86_64"
grep -q 'type="application/x-blazanim"' "$L/blazma-animation-mime.xml" && ok "shared-mime-info XML for application/x-blazanim shipped" || bad "mime XML missing"
[ -s "$L/LICENSE.txt" ] && [ -s "$L/legal/GODOT_COPYRIGHT.txt" ] && ok "licence files in the tarball" || bad "licence files missing from the tarball"
if [ -f "$D/kimodo-runner-$V-linux-x64.tar.gz" ]; then
  [ -x "$L/tools/kimodo/kimodo-runner" ] && ok "tools/kimodo/kimodo-runner present (runner staged)" || bad "runner staged but tools/kimodo/kimodo-runner missing"
  ls "$L/tools/kimodo/" | grep -q '^libggml-cpu-x64\.so' && ls "$L/tools/kimodo/" | grep -q '^libggml-vulkan\.so' && ok "Linux GGML backends present" || bad "Linux runner lacks libggml-cpu-*/libggml-vulkan"
fi
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
