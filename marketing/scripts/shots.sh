#!/usr/bin/env bash
# phase 2: feature screenshots at 1920x1080, UI scale 1.25, Arabic dark (selftest scenarios)
G=/opt/godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64
R=${BLAZMA_REEL:-$HOME/blazma-reel}; OUT=$R/${OUTDIR:-shots1080}; mkdir -p $OUT
run() { # name, env...
  n=$1; shift; D=$R/xdg-$n; rm -rf $D; mkdir -p $D/d $D/c $D/k $OUT/$n
  env XDG_DATA_HOME=$D/d XDG_CONFIG_HOME=$D/c XDG_CACHE_HOME=$D/k BLAZMA_UI_SCALE=${UI:-1.25} "$@" xvfb-run -a -s "-screen 0 ${W:-1920}x${H:-1080}x24" nice -n 19 $G --path ${BLAZMA_REPO:-$HOME/blazma-Animation}/app/godot --rendering-driver opengl3 --audio-driver Dummy --resolution ${W:-1920}x${H:-1080} -- --selftest=$OUT/$n > $OUT/$n.log 2>&1
  echo "$n rc=$?"
}
for j in "$@"; do
  case $j in
    cam) run cam BLAZMA_ONLY_CAM=1;;
    p46) run p46 BLAZMA_ONLY_PHASES=4,6;;
    p7) run p7 BLAZMA_ONLY_PHASES=7;;
    p8) run p8 BLAZMA_ONLY_P8=1;;
    p9) run p9 BLAZMA_ONLY_P9=1;;
    reelmodels) run reelmodels BLAZMA_ONLY_REELMODELS=1 BLAZMA_REEL_MODELS_DIR=${BLAZMA_MODELS:-$HOME/BlazmaModels};;
    models) run models BLAZMA_ONLY_MODELS=1 BLAZMA_REEL_MODELS_DIR=${BLAZMA_MODELS:-$HOME/BlazmaModels};;
  esac
done
