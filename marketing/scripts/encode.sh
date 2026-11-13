#!/usr/bin/env bash
# delivery encode: two-pass x264 (slow, tune film), yuv420p, faststart, ~18 MB for 60 s
cd ${BLAZMA_REEL:-$HOME/blazma-reel}/out/blazma-explainer; FPS=${FPS:-60}; VB=${VB:-2250k}
V=video_16x9.mp4; A=${BLAZMA_REEL:-$HOME/blazma-reel}/blazma-explainer/audio/mix.wav
ffmpeg -v error -y -i $V -vf fps=$FPS -c:v libx264 -preset slow -tune ${TUNE:-film} -b:v $VB -maxrate 5000k -bufsize 8000k -pix_fmt yuv420p -pass 1 -passlogfile ${TMPDIR:-/tmp}/x264pass -an -f null /dev/null
ffmpeg -v error -y -i $V -i $A -vf fps=$FPS -c:v libx264 -preset slow -tune ${TUNE:-film} -b:v $VB -maxrate 5000k -bufsize 8000k -pix_fmt yuv420p -pass 2 -passlogfile ${TMPDIR:-/tmp}/x264pass -c:a aac -b:a 128k -shortest -movflags +faststart delivery.mp4
ls -l delivery.mp4
