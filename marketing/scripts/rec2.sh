#!/usr/bin/env bash
# phase 3: native 1920x1080 / 60 fps lossless (PNG) recording of the hero demo, re-using hero.blazanim (no regeneration)
G=/opt/godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64
R=/home/user/blazma-reel; O=$R/xdg-rec; rm -rf $O $R/rawpng; mkdir -p $O/d $O/c $O/k $R/rawpng
export XDG_DATA_HOME=$O/d XDG_CONFIG_HOME=$O/c XDG_CACHE_HOME=$O/k BLAZMA_DEMO=2 BLAZMA_DEMO_PROVIDER=kimodo BLAZMA_DEMO_SAVE=/home/user/BlazmaProjects/hero.blazanim BLAZMA_DEMO_CAPTIONS=0 BLAZMA_UI_SCALE=${UI:-1.25} BLAZMA_DEMO_FRAME=${FRAME:-1.25}
export BLAZMA_KIMODO_RUNNER=/home/user/blazma-Animation/ai/kimodo/build-dl/bin/kimodo-runner BLAZMA_KIMODO_MODELS=/root/BlazmaModels BLAZMA_KIMODO_MODEL=soma-rp-v1.1 BLAZMA_KIMODO_TEXT_MODEL=Llama-3-Kimodo-Q4_K_M.gguf BLAZMA_KIMODO_DEVICE=cpu
xvfb-run -a -s "-screen 0 ${W:-1920}x${H:-1080}x24" $G --path /home/user/blazma-Animation/app/godot --rendering-driver opengl3 --audio-driver Dummy --resolution ${W:-1920}x${H:-1080} --write-movie $R/rawpng/f.png --fixed-fps 60 > $R/rec.log 2>&1
echo rc=$? >> $R/rec.log
