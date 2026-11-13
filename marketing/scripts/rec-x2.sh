#!/usr/bin/env bash
# x2 takes (rec-x2: UI scale 1.6 for the dialog; rec-x3: UISCALE=1.0 FRAMEBOX=1.15 for the run): human demo, real clicks, visible pointer; re-uses the saved real Kimodo hero
G=/opt/godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64
R=${BLAZMA_REEL:-$HOME/blazma-reel}; O=$R/xdg-x2; mkdir -p $O/d $O/c $O/k $R/rawx2
export XDG_DATA_HOME=$O/d XDG_CONFIG_HOME=$O/c XDG_CACHE_HOME=$O/k BLAZMA_DEMO=2 BLAZMA_DEMO_HUMAN=1 BLAZMA_DEMO_PROVIDER=kimodo BLAZMA_DEMO_SAVE=$HOME/BlazmaProjects/hero.blazanim BLAZMA_DEMO_CAPTIONS=0 BLAZMA_UI_SCALE=${UISCALE:-1.6} BLAZMA_DEMO_FRAME=${FRAMEBOX:-1.1}
export BLAZMA_KIMODO_RUNNER=${BLAZMA_REPO:-$HOME/blazma-Animation}/ai/kimodo/build-dl/bin/kimodo-runner BLAZMA_KIMODO_MODELS=${BLAZMA_MODELS:-$HOME/BlazmaModels} BLAZMA_KIMODO_MODEL=soma-rp-v1.1 BLAZMA_KIMODO_TEXT_MODEL=Llama-3-Kimodo-Q4_K_M.gguf BLAZMA_KIMODO_DEVICE=cpu
xvfb-run -a -s "-screen 0 1920x1080x24" $G --path ${BLAZMA_REC_REPO:-$HOME/blazma-rec}/app/godot --rendering-driver opengl3 --audio-driver Dummy --resolution 1920x1080 --write-movie $R/rawx2/f.png --fixed-fps 60 > $R/rec-x2.log 2>&1
echo rc=$? >> $R/rec-x2.log
