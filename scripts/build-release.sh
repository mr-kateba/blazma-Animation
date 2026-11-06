#!/usr/bin/env bash
# Blazma Animation — the single release build. Runs entirely locally (no CI needed); .github/workflows/release.yml calls this same script.
#
# Usage: scripts/build-release.sh <version> [--skip-tests] [--skip-linux] [--skip-appimage] [--skip-installer] [--from-git[=REV]]
#   <version>   e.g. 0.9.0 or 1.0.0-rc1  (becomes the file names and the app/installer version)
# Environment:
#   GODOT          Godot .NET editor binary (default: /opt/godot/...)
#   --from-git     build a clean copy of a commit (default HEAD) instead of the working tree: uncommitted or half-finished edits can't leak into a release
#   BLAZMA_SRC     source tree to build (default: this repo). Use it to build a clean copy, e.g. `git archive HEAD | tar -x -C DIR`.
#   APPIMAGETOOL   path to appimagetool (default: downloaded once into build/cache from the official AppImage GitHub releases)
#   SKIP_TESTS=1   same as --skip-tests
# Output: build/release/<version>/   (zip, setup.exe, tar.gz, AppImage, SHA256SUMS.txt, RELEASE_NOTES-<version>.md)
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"   # tooling (packaging/, docs/reports) always comes from here
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
dotnet build "$SRC/app/godot" -v quiet -nologo 2>&1 | tail -2 || true
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

# ---------------------------------------------------------------- 4. assemble
DOCS=(AGENT_SETUP_AR.md AI_INTEGRATION.md MCP_TOOLS.md CLI_REFERENCE.md AGENT_RECIPES.md)
add_common() { # stage-dir
  local d="$1"
  mkdir -p "$d/docs"
  for f in "${DOCS[@]}"; do [ -f "$ROOT/docs/$f" ] && cp "$ROOT/docs/$f" "$d/docs/"; done
  [ -d "$ROOT/docs/recipes" ] && cp -r "$ROOT/docs/recipes" "$d/docs/recipes"
  cp "$ROOT/packaging/legal/THIRD_PARTY_NOTICES.md" "$ROOT/packaging/legal/GODOT_LICENSE.txt" "$d/"
  cp "$ROOT/app/godot/assets/fonts/OFL-IBM-Plex.txt" "$d/"
  [ -f "$ROOT/LICENSE" ] && cp "$ROOT/LICENSE" "$d/LICENSE.txt" || true
  { echo "Blazma Animation $VERSION"; echo "Built $(date -u +%Y-%m-%dT%H:%M:%SZ) from $GITREV"; } > "$d/VERSION.txt"
}

log "assemble Windows stage"
WSTAGE="$WORK/stage/$WINNAME"; mkdir -p "$WSTAGE"
cp "$WORK/win/BlazmaAnimation.exe" "$WORK/win/BlazmaAnimation.pck" "$WSTAGE/"
[ -f "$WORK/win/BlazmaAnimation.console.exe" ] && cp "$WORK/win/BlazmaAnimation.console.exe" "$WSTAGE/"
cp -r "$WIN_DATA" "$WSTAGE/"; find "$WSTAGE" -name '*.pdb' -delete
mkdir -p "$WSTAGE/tools"; cp "$WORK/tools-win/blazma-mcp.exe" "$WORK/tools-win/blazma-anim.exe" "$WSTAGE/tools/"
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
  makensis -V2 -DVERSION="$VERSION" -DNUMVER="$NUMVER" -DSRCDIR="$WSTAGE" -DREPO="$ROOT" \
    -DOUTFILE="$OUT/BlazmaAnimation-$VERSION-setup.exe" "$ROOT/packaging/windows/installer.nsi" || die "makensis failed"
fi

if [ $SKIP_LINUX = 0 ]; then
  log "assemble Linux stage + tar.gz"
  LSTAGE="$WORK/stage/$LINNAME"; mkdir -p "$LSTAGE/tools"
  cp "$WORK/linux/BlazmaAnimation.x86_64" "$WORK/linux/BlazmaAnimation.pck" "$LSTAGE/"
  cp -r "$WORK/linux/data_Blazma.UI_linuxbsd_x86_64" "$LSTAGE/"; find "$LSTAGE" -name '*.pdb' -delete
  chmod +x "$LSTAGE/BlazmaAnimation.x86_64"
  cp "$WORK/tools-linux/blazma-mcp" "$WORK/tools-linux/blazma-anim" "$LSTAGE/tools/"
  add_common "$LSTAGE"
  cp "$ROOT/packaging/linux/blazma-animation.desktop" "$ROOT/packaging/assets/blazma.png" "$LSTAGE/"
  # AppImage first (it must not contain portable.txt), then the portable tarball
  if [ $SKIP_APPIMAGE = 0 ]; then
    log "AppImage"
    AT="${APPIMAGETOOL:-$CACHE/appimagetool-x86_64.AppImage}"
    if [ ! -x "$AT" ]; then
      curl -sSfL -o "$AT" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage && chmod +x "$AT" \
        || { echo "WARNING: could not get appimagetool — AppImage skipped"; SKIP_APPIMAGE=1; }
    fi
  fi
  if [ $SKIP_APPIMAGE = 0 ]; then
    AD="$WORK/AppDir"; mkdir -p "$AD/usr/bin"
    cp -r "$LSTAGE"/. "$AD/usr/bin/"
    rm -f "$AD/usr/bin/blazma-animation.desktop" "$AD/usr/bin/blazma.png"
    cp "$ROOT/packaging/linux/blazma-animation.desktop" "$AD/blazma-animation.desktop"
    sed -i 's|^Exec=.*|Exec=BlazmaAnimation.x86_64 %F|' "$AD/blazma-animation.desktop"
    cp "$ROOT/packaging/assets/blazma.png" "$AD/blazma-animation.png"; cp "$AD/blazma-animation.png" "$AD/.DirIcon"
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
  echo "- تحقق من الملفات بـ \`SHA256SUMS.txt\`."
  echo
  echo "## ما تم إنجازه (من تقارير المراحل)"
  for r in "$ROOT"/docs/reports/PHASE*.md; do
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
