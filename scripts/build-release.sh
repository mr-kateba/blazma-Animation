#!/usr/bin/env bash
# Blazma Animation — the single release build. Runs entirely locally (no CI needed); .github/workflows/release.yml calls this same script.
#
# Usage: scripts/build-release.sh <version> [--skip-tests] [--skip-linux] [--skip-appimage] [--skip-installer] [--from-git[=REV]]
#   <version>   e.g. 0.9.0 or 1.0.0-rc1  (becomes the file names and the app/installer version)
# Environment:
#   GODOT          Godot .NET editor binary (default: /opt/godot/...)
#   --from-git     build a clean copy of a commit (default HEAD) instead of the working tree: uncommitted or half-finished edits can't leak into a release
#   BLAZMA_SRC     source tree to build (default: this repo). Use it to build a clean copy, e.g. `git archive HEAD | tar -x -C DIR`.
#   APPIMAGETOOL   path to appimagetool (default: release $APPIMAGETOOL_VERSION downloaded once into build/cache from the official
#                  AppImage GitHub releases and checked against $APPIMAGETOOL_SHA256; a mismatch stops the build)
#   SKIP_TESTS=1   same as --skip-tests
#   KIMODO_RUNNER_WIN / KIMODO_RUNNER_LINUX   optional: folder (or .zip) with a kimodo-runner build (the artifacts of the manual
#                  `kimodo-runner` workflow: kimodo-runner[.exe], GGML libraries, SHA256SUMS.txt). Default: build/cache/kimodo-runner-win64
#                  and build/cache/kimodo-runner-linux-x64 when present. Bundled into tools/kimodo/ (the app finds it there: the repo
#                  is private, so its release assets can't be downloaded anonymously) and also published as kimodo-runner-<version>-*.zip/.tar.gz.
#                  Missing = the release has no runner (Kimodo then needs a runner the user builds; docs/AI_INTEGRATION.md §5).
# Output: build/release/<version>/   (zip, setup.exe, tar.gz, AppImage, SHA256SUMS.txt, RELEASE_NOTES-<version>.md)
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"   # tooling (scripts, installer script, export presets) always comes from here
APPIMAGETOOL_VERSION=1.9.0
APPIMAGETOOL_SHA256=46fdd785094c7f6e545b61afcfb0f3d98d8eab243f644b4b17698c01d06083d1
SRC="$(cd "${BLAZMA_SRC:-$ROOT}" && pwd)"                  # the code that gets built
G="${GODOT:-/opt/godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64}"
TPL="$HOME/.local/share/godot/export_templates/4.7.2.stable.mono"

VERSION="${1:-}"; shift || true
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.]+)?$ ]] || { echo "usage: $0 <version> (like 0.9.0 or 1.0.0-rc1) [--skip-tests] [--skip-linux] [--skip-appimage] [--skip-installer] [--from-git[=REV]]" >&2; exit 2; }
NUMVER="${VERSION%%-*}"; NUMVER="$(echo "$NUMVER" | cut -d. -f1-3)"
SKIP_TESTS="${SKIP_TESTS:-0}"; SKIP_LINUX=0; SKIP_APPIMAGE=0; SKIP_INSTALLER=0; FROM_GIT=""
for a in "$@"; do case "$a" in
  --from-git) FROM_GIT=HEAD;; --from-git=*) FROM_GIT="${a#*=}";;
  --skip-tests) SKIP_TESTS=1;; --skip-linux) SKIP_LINUX=1;; --skip-appimage) SKIP_APPIMAGE=1;; --skip-installer) SKIP_INSTALLER=1;;
  *) echo "unknown option $a" >&2; exit 2;; esac; done

OUT="$ROOT/build/release/$VERSION"
WORK="$ROOT/build/release/_work/$VERSION"
CACHE="$ROOT/build/cache"
WINNAME="BlazmaAnimation-$VERSION-win64"
LINNAME="BlazmaAnimation-$VERSION-linux-x64"
log() { echo; echo "=== $* ==="; }
die() { echo "ERROR: $*" >&2; exit 1; }

[ -x "$G" ] || die "Godot not found at $G (set GODOT=...)"
[ -f "$TPL/windows_release_x86_64.exe" ] || die "Windows export templates missing in $TPL"
if [ $SKIP_LINUX = 0 ] && [ ! -f "$TPL/linux_release.x86_64" ]; then
  echo "WARNING: Linux export templates missing in $TPL — skipping Linux artifacts (see docs/RELEASING.md)"; SKIP_LINUX=1; fi
[ $SKIP_INSTALLER = 1 ] || command -v makensis >/dev/null || die "makensis not found (apt-get install -y nsis) or pass --skip-installer"

rm -rf "$OUT" "$WORK"; mkdir -p "$OUT" "$WORK" "$CACHE"

if [ -n "$FROM_GIT" ]; then
  log "clean copy of $FROM_GIT"
  mkdir -p "$WORK/src"; git -C "$SRC" archive "$FROM_GIT" | tar -x -C "$WORK/src"
  cp "$ROOT/app/godot/export_presets.cfg" "$WORK/src/app/godot/export_presets.cfg"   # presets belong to the release tooling
  SRC="$WORK/src"; echo "building $(git -C "$ROOT" rev-parse --short "$FROM_GIT") from $SRC"
  GITREV="$(git -C "$ROOT" rev-parse --short "$FROM_GIT")"
else
  GITREV="$(git -C "$SRC" rev-parse --short HEAD 2>/dev/null || echo unknown)$(git -C "$SRC" diff --quiet HEAD 2>/dev/null || echo '+local-changes')"
fi

# ---------------------------------------------------------------- 1. core tests
if [ "$SKIP_TESTS" = 1 ]; then log "core tests SKIPPED"; else
  log "core tests (dotnet test, warnings are errors)"
  (cd "$SRC" && dotnet test Blazma.slnx -warnaserror) || die "core tests failed — no release"
fi

# ---------------------------------------------------------------- 2. Godot exports
export_preset() { # preset-name, out-dir, file-name
  mkdir -p "$2"
  "$G" --headless --path "$SRC/app/godot" --export-release "$1" "$2/$3" 2>&1 | tee "$WORK/export-$1.log" | grep -vE "savepack|^\s*(\x1b\[0m)?$" | tail -15
  [ -f "$2/$3" ] || die "export '$1' produced no $3 (see $WORK/export-$1.log)"
}
log "build C# (so the import can load the autoload scripts)"
dotnet build "$SRC/app/godot" -v quiet -nologo >"$WORK/build-app.log" 2>&1 || { tail -30 "$WORK/build-app.log"; die "the app's C# build failed (see $WORK/build-app.log)"; }
log "godot import"
"$G" --headless --path "$SRC/app/godot" --import >"$WORK/import.log" 2>&1 || true

log "export: Windows x64"
export_preset "Windows Desktop" "$WORK/win" BlazmaAnimation.exe
WIN_DATA="$WORK/win/data_Blazma.UI_windows_x86_64"
for f in BlazmaAnimation.pck "data_Blazma.UI_windows_x86_64/Blazma.UI.dll" "data_Blazma.UI_windows_x86_64/Blazma.Core.dll" "data_Blazma.UI_windows_x86_64/coreclr.dll"; do
  [ -f "$WORK/win/$f" ] || die "Windows export is missing $f — the app could not start"; done

if [ $SKIP_LINUX = 0 ]; then
  log "export: Linux x64"
  export_preset "Linux" "$WORK/linux" BlazmaAnimation.x86_64
  for f in BlazmaAnimation.pck "data_Blazma.UI_linuxbsd_x86_64/Blazma.UI.dll" "data_Blazma.UI_linuxbsd_x86_64/Blazma.Core.dll"; do
    [ -f "$WORK/linux/$f" ] || die "Linux export is missing $f"; done
fi

# ---------------------------------------------------------------- 3. agent tools (self-contained single file)
publish_tools() { # rid, outdir
  for p in mcp cli; do
    dotnet publish "$SRC/agent/$p" -c Release -r "$1" --self-contained \
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false \
      -o "$2" -v quiet -nologo | tail -3
  done
}
log "publish tools win-x64"; publish_tools win-x64 "$WORK/tools-win"
test -f "$WORK/tools-win/blazma-mcp.exe" && test -f "$WORK/tools-win/blazma-anim.exe" || die "Windows tools missing"
if [ $SKIP_LINUX = 0 ]; then
  log "publish tools linux-x64"; publish_tools linux-x64 "$WORK/tools-linux"
  test -f "$WORK/tools-linux/blazma-mcp" && test -f "$WORK/tools-linux/blazma-anim" || die "Linux tools missing"
  chmod +x "$WORK/tools-linux/blazma-mcp" "$WORK/tools-linux/blazma-anim"
fi

# ---------------------------------------------------------------- 3b. optional kimodo-runner (never model weights)
stage_runner() { # source (dir or zip), exe name, dest-dir → copies runner + libraries, checks SHA256SUMS.txt if present
  local src="$1" exe="$2" dst="$3" tmp
  if [ -f "$src" ] && [[ "$src" == *.zip ]]; then tmp="$WORK/runner-unzip-$exe"; rm -rf "$tmp"; mkdir -p "$tmp"; unzip -q "$src" -d "$tmp"; src="$tmp"; fi
  local dir; dir="$(dirname "$(find "$src" -type f -name "$exe" | head -1)")"
  [ -f "$dir/$exe" ] || die "kimodo-runner: no $exe in $1"
  if [ -f "$dir/SHA256SUMS.txt" ]; then (cd "$dir" && sha256sum -c --quiet SHA256SUMS.txt) || die "kimodo-runner: checksum mismatch in $1"; fi
  find "$dir" -maxdepth 1 -type f -name '*.gguf' | grep -q . && die "kimodo-runner: $1 contains model weights — never bundle them"
  mkdir -p "$dst"
  find "$dir" -maxdepth 1 \( -type f -o -type l \) \( -name "$exe" -o -name '*.dll' -o -name '*.so*' -o -name SHA256SUMS.txt \) -exec cp -P {} "$dst/" \;
  [ "$exe" = kimodo-runner ] && chmod +x "$dst/$exe"
  echo "kimodo-runner from $1: $(ls "$dst" | tr '\n' ' ')"
}
RUNNER_WIN="${KIMODO_RUNNER_WIN:-$CACHE/kimodo-runner-win64}"; RUNNER_LINUX="${KIMODO_RUNNER_LINUX:-$CACHE/kimodo-runner-linux-x64}"
HAVE_RUNNER_WIN=0; HAVE_RUNNER_LINUX=0
if [ -e "$RUNNER_WIN" ]; then log "kimodo-runner (Windows)"; stage_runner "$RUNNER_WIN" kimodo-runner.exe "$WORK/runner-win"; HAVE_RUNNER_WIN=1
else echo "NOTE: no Windows kimodo-runner at $RUNNER_WIN — the release will not include it"; fi
if [ $SKIP_LINUX = 0 ] && [ -e "$RUNNER_LINUX" ]; then log "kimodo-runner (Linux)"; stage_runner "$RUNNER_LINUX" kimodo-runner "$WORK/runner-linux"; HAVE_RUNNER_LINUX=1; fi

# ---------------------------------------------------------------- 4. assemble
# Docs and legal texts come from the source that was built ($SRC: the git snapshot with --from-git), never from local edits.
DOCS=(AGENT_SETUP_AR.md AI_INTEGRATION.md MCP_TOOLS.md CLI_REFERENCE.md AGENT_RECIPES.md)
add_common() { # stage-dir
  local d="$1"
  mkdir -p "$d/docs" "$d/legal"
  for f in "${DOCS[@]}"; do [ -f "$SRC/docs/$f" ] && cp "$SRC/docs/$f" "$d/docs/"; done
  [ -d "$SRC/docs/recipes" ] && cp -r "$SRC/docs/recipes" "$d/docs/recipes"
  [ -f "$SRC/LICENSE" ] || die "LICENSE missing in $SRC"
  cp "$SRC/LICENSE" "$d/LICENSE.txt"
  cp "$SRC/packaging/legal/THIRD_PARTY_NOTICES.md" "$d/"
  find "$SRC/packaging/legal" -maxdepth 1 -type f ! -name THIRD_PARTY_NOTICES.md -exec cp {} "$d/legal/" \;
  cp "$SRC/app/godot/assets/fonts/OFL-IBM-Plex.txt" "$d/legal/"
  { echo "Blazma Animation $VERSION"; echo "Built $(date -u +%Y-%m-%dT%H:%M:%SZ) from $GITREV"; } > "$d/VERSION.txt"
}

log "assemble Windows stage"
WSTAGE="$WORK/stage/$WINNAME"; mkdir -p "$WSTAGE"
cp "$WORK/win/BlazmaAnimation.exe" "$WORK/win/BlazmaAnimation.pck" "$WSTAGE/"
[ -f "$WORK/win/BlazmaAnimation.console.exe" ] && cp "$WORK/win/BlazmaAnimation.console.exe" "$WSTAGE/"
cp -r "$WIN_DATA" "$WSTAGE/"; find "$WSTAGE" -name '*.pdb' -delete
mkdir -p "$WSTAGE/tools"; cp "$WORK/tools-win/blazma-mcp.exe" "$WORK/tools-win/blazma-anim.exe" "$WSTAGE/tools/"
if [ $HAVE_RUNNER_WIN = 1 ]; then
  cp -r "$WORK/runner-win" "$WSTAGE/tools/kimodo"
  (cd "$WORK" && rm -rf "kimodo-runner-$VERSION-win64" && cp -r runner-win "kimodo-runner-$VERSION-win64" && zip -q -r -9 -X "$OUT/kimodo-runner-$VERSION-win64.zip" "kimodo-runner-$VERSION-win64")
fi
add_common "$WSTAGE"

log "Windows portable zip"
# portable.txt exists only in the zip, never in the installed copy
cat > "$WSTAGE/portable.txt" <<'TXT'
Portable mode marker. While this file sits next to BlazmaAnimation.exe, settings, autosave recovery and logs
are kept in the "userdata" folder next to the exe instead of %APPDATA%\BlazmaAnimation.
Delete this file to use the normal per-user folder.
TXT
(cd "$WORK/stage" && zip -q -r -9 -X "$OUT/$WINNAME.zip" "$WINNAME")
rm "$WSTAGE/portable.txt"

if [ $SKIP_INSTALLER = 0 ]; then
  log "Windows installer (NSIS)"
  # Exactly what the installer puts into $INSTDIR, so the uninstaller removes those names and nothing else.
  UNLIST="$WORK/uninstall-list.nsh"
  { find "$WSTAGE" -mindepth 1 -maxdepth 1 -type f -printf 'Delete "$INSTDIR\\%f"\n'
    find "$WSTAGE" -mindepth 1 -maxdepth 1 -type d -printf 'RMDir /r "$INSTDIR\\%f"\n'; } | sort > "$UNLIST"
  grep -q portable.txt "$UNLIST" && die "portable.txt must not be in the installer payload"
  makensis -V2 -DVERSION="$VERSION" -DNUMVER="$NUMVER" -DSRCDIR="$WSTAGE" -DREPO="$ROOT" -DUNINSTLIST="$UNLIST" \
    -DOUTFILE="$OUT/BlazmaAnimation-$VERSION-setup.exe" "$ROOT/packaging/windows/installer.nsi" || die "makensis failed"
fi

if [ $SKIP_LINUX = 0 ]; then
  log "assemble Linux stage + tar.gz"
  LSTAGE="$WORK/stage/$LINNAME"; mkdir -p "$LSTAGE/tools"
  cp "$WORK/linux/BlazmaAnimation.x86_64" "$WORK/linux/BlazmaAnimation.pck" "$LSTAGE/"
  cp -r "$WORK/linux/data_Blazma.UI_linuxbsd_x86_64" "$LSTAGE/"; find "$LSTAGE" -name '*.pdb' -delete
  chmod +x "$LSTAGE/BlazmaAnimation.x86_64"
  cp "$WORK/tools-linux/blazma-mcp" "$WORK/tools-linux/blazma-anim" "$LSTAGE/tools/"
  if [ $HAVE_RUNNER_LINUX = 1 ]; then
    cp -r "$WORK/runner-linux" "$LSTAGE/tools/kimodo"
    tar --sort=name --owner=0 --group=0 --numeric-owner -C "$WORK" --transform "s|^runner-linux|kimodo-runner-$VERSION-linux-x64|" -czf "$OUT/kimodo-runner-$VERSION-linux-x64.tar.gz" runner-linux
  fi
  add_common "$LSTAGE"
  cp "$SRC/packaging/linux/blazma-animation.desktop" "$SRC/packaging/linux/blazma-animation-mime.xml" "$SRC/packaging/assets/blazma.png" "$LSTAGE/"
  # AppImage first (it must not contain portable.txt), then the portable tarball
  if [ $SKIP_APPIMAGE = 0 ]; then
    log "AppImage"
    AT="${APPIMAGETOOL:-$CACHE/appimagetool-$APPIMAGETOOL_VERSION-x86_64.AppImage}"
    if [ -z "${APPIMAGETOOL:-}" ]; then
      if [ ! -f "$AT" ] || ! echo "$APPIMAGETOOL_SHA256  $AT" | sha256sum -c --quiet - 2>/dev/null; then
        rm -f "$AT"
        curl -sSfL -o "$AT.part" "https://github.com/AppImage/appimagetool/releases/download/$APPIMAGETOOL_VERSION/appimagetool-x86_64.AppImage" \
          || { rm -f "$AT.part"; echo "WARNING: could not download appimagetool $APPIMAGETOOL_VERSION — AppImage skipped"; SKIP_APPIMAGE=1; }
        if [ $SKIP_APPIMAGE = 0 ]; then
          echo "$APPIMAGETOOL_SHA256  $AT.part" | sha256sum -c --quiet - || { rm -f "$AT.part"; die "appimagetool download does not match the pinned SHA-256"; }
          mv "$AT.part" "$AT"
        fi
      fi
      [ $SKIP_APPIMAGE = 1 ] || chmod +x "$AT"
    fi
  fi
  if [ $SKIP_APPIMAGE = 0 ]; then
    AD="$WORK/AppDir"; mkdir -p "$AD/usr/bin"
    cp -r "$LSTAGE"/. "$AD/usr/bin/"
    rm -f "$AD/usr/bin/blazma-animation.desktop" "$AD/usr/bin/blazma-animation-mime.xml" "$AD/usr/bin/blazma.png"
    cp "$SRC/packaging/linux/blazma-animation.desktop" "$AD/blazma-animation.desktop"
    mkdir -p "$AD/usr/share/mime/packages"; cp "$SRC/packaging/linux/blazma-animation-mime.xml" "$AD/usr/share/mime/packages/blazma-animation.xml"
    cp "$SRC/packaging/assets/blazma.png" "$AD/blazma-animation.png"; cp "$AD/blazma-animation.png" "$AD/.DirIcon"
    cat > "$AD/AppRun" <<'RUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/BlazmaAnimation.x86_64" "$@"
RUN
    chmod +x "$AD/AppRun"
    (cd "$WORK" && ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$AT" --no-appstream "$AD" "$OUT/BlazmaAnimation-$VERSION-x86_64.AppImage" >"$WORK/appimage.log" 2>&1) \
      || { tail -5 "$WORK/appimage.log"; echo "WARNING: AppImage build failed — skipped"; }
  fi
  cat > "$LSTAGE/portable.txt" <<'TXT'
Portable mode marker (see docs). Settings and autosave live in the "userdata" folder next to BlazmaAnimation.x86_64.
Delete this file to use ~/.local/share/BlazmaAnimation.
TXT
  tar --sort=name --owner=0 --group=0 --numeric-owner -C "$WORK/stage" -czf "$OUT/$LINNAME.tar.gz" "$LINNAME"
fi

# ---------------------------------------------------------------- 5. release notes (from docs/reports)
log "release notes"
NOTES="$OUT/RELEASE_NOTES-$VERSION.md"
{
  echo "# Blazma Animation $VERSION"
  echo
  echo "## التثبيت"
  echo "- **Windows (مثبّت):** شغّل \`BlazmaAnimation-$VERSION-setup.exe\`، تثبيت للمستخدم الحالي بدون صلاحيات مدير في \`%LOCALAPPDATA%\\Programs\\BlazmaAnimation\`."
  echo "- **Windows (محمول):** فك \`$WINNAME.zip\` وشغّل \`BlazmaAnimation.exe\`. الإعدادات بجانب البرنامج إذا وُجد \`portable.txt\`."
  echo "- **Linux:** \`BlazmaAnimation-$VERSION-x86_64.AppImage\` (chmod +x ثم تشغيل) أو \`$LINNAME.tar.gz\`."
  echo "- لا يحتاج .NET ولا Godot. البرنامج غير موقَّع رقميًا: في Windows اضغط **More info** ثم **Run anyway** في SmartScreen."
  echo "- أدوات الذكاء الاصطناعي في مجلد \`tools\`؛ الشرح في \`docs/AGENT_SETUP_AR.md\`."
  [ $HAVE_RUNNER_WIN = 1 ] && echo "- \`kimodo-runner\` (توليد الحركة محليًا) مضمَّن في \`tools/kimodo\`. نماذج Kimodo لا تُضمَّن: تُنزَّل من داخل البرنامج (الإعدادات ← AI) بضغطة من المستخدم بعد عرض الحجم والتراخيص."
  echo "- تحقق من الملفات بـ \`SHA256SUMS.txt\`."
  echo
  echo "## ما تم إنجازه (من تقارير المراحل)"
  for r in "$SRC"/docs/reports/PHASE*.md; do
    [ -f "$r" ] || continue
    echo
    awk '
      /^# /        { sub(/^# /, "### "); print; next }
      /^التاريخ/   { print; next }
      /^## (الاختبارات|قيود معروفة)/ { show=1; print "#" $0; next }
      /^## /       { show=0; next }
      show         { print }
    ' "$r"
  done
} > "$NOTES"

# ---------------------------------------------------------------- 6. checksums
log "SHA256SUMS"
(cd "$OUT" && find . -maxdepth 1 -type f ! -name SHA256SUMS.txt -printf '%f\n' | sort | xargs sha256sum > SHA256SUMS.txt && cat SHA256SUMS.txt)

log "artifacts in $OUT"
ls -lh "$OUT"
echo "Next: scripts/release-smoke.sh $VERSION"
