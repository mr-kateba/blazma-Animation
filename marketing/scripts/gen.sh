#!/usr/bin/env bash
# phase 1: real Kimodo generation of the hero prompt, saved once to hero.blazanim (re-used by every re-take)
G=/opt/godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64
O=${BLAZMA_REEL:-$HOME/blazma-reel}/xdg-gen; rm -rf $O; mkdir -p $O/d $O/c $O/k
export XDG_DATA_HOME=$O/d XDG_CONFIG_HOME=$O/c XDG_CACHE_HOME=$O/k BLAZMA_DEMO=2 BLAZMA_DEMO_PROVIDER=kimodo BLAZMA_DEMO_GENONLY=1 BLAZMA_DEMO_SAVE=${BLAZMA_REEL:-$HOME/blazma-reel}/hero.blazanim
export BLAZMA_KIMODO_RUNNER=${BLAZMA_REPO:-$HOME/blazma-Animation}/ai/kimodo/build-dl/bin/kimodo-runner BLAZMA_KIMODO_MODELS=${BLAZMA_MODELS:-$HOME/BlazmaModels} BLAZMA_KIMODO_MODEL=soma-rp-v1.1 BLAZMA_KIMODO_TEXT_MODEL=Llama-3-Kimodo-Q4_K_M.gguf BLAZMA_KIMODO_DEVICE=cpu BLAZMA_KIMODO_TIMEOUT_SECONDS=7200
xvfb-run -a -s "-screen 0 1280x720x24" $G --path ${BLAZMA_REPO:-$HOME/blazma-Animation}/app/godot --rendering-driver opengl3 --audio-driver Dummy --resolution 1280x720 > ${BLAZMA_REEL:-$HOME/blazma-reel}/gen.log 2>&1
echo rc=$? >> ${BLAZMA_REEL:-$HOME/blazma-reel}/gen.log
