#!/usr/bin/env bash
# Screen-recording-style take for the X film (marketing/blazma-x): real pointer, real clicks, human typing with one corrected slip.
# 1) copy app/ and src/ (tracked files) to a scratch folder and apply the recorder patch:  patch -p1 < marketing/scripts/demo-human.patch
# 2) in that copy set app/godot/project.godot  window/size/viewport_width=1920, viewport_height=1080  (Godot ignores --resolution under --write-movie otherwise), then: dotnet build app/godot
# 3) run this script with SRC=<that copy>/app/godot
G=${G:-/opt/godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64}
SRC=${SRC:?path to the patched app/godot}; R=${R:-$PWD}; O=$R/xdg-x; rm -rf $O $R/rawx; mkdir -p $O/d $O/c $O/k $R/rawx
export XDG_DATA_HOME=$O/d XDG_CONFIG_HOME=$O/c XDG_CACHE_HOME=$O/k BLAZMA_DEMO=2 BLAZMA_DEMO_HUMAN=1 BLAZMA_DEMO_PROVIDER=kimodo BLAZMA_DEMO_SAVE=${HERO:-$HOME/BlazmaProjects/hero.blazanim} BLAZMA_DEMO_CAPTIONS=0 BLAZMA_UI_SCALE=1.25 BLAZMA_DEMO_FRAME=1.25
export BLAZMA_KIMODO_RUNNER=${KIMODO_RUNNER:-$PWD/ai/kimodo/build-dl/bin/kimodo-runner} BLAZMA_KIMODO_MODELS=${KIMODO_MODELS:-$HOME/BlazmaModels} BLAZMA_KIMODO_MODEL=soma-rp-v1.1 BLAZMA_KIMODO_TEXT_MODEL=Llama-3-Kimodo-Q4_K_M.gguf BLAZMA_KIMODO_DEVICE=cpu
xvfb-run -a -s "-screen 0 1920x1080x24" $G --path $SRC --rendering-driver opengl3 --audio-driver Dummy --resolution 1920x1080 --write-movie $R/rawx/f.png --fixed-fps 60 > $R/rec-x.log 2>&1
echo rc=$? >> $R/rec-x.log   # marks (clicks, typing, plan) are in rec-x.log: blazma-x/tools/analyze.py reads them
