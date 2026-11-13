#!/usr/bin/env bash
# Delivery encode for X: H.264 High yuv420p, two-pass, faststart, 30 fps, AAC 128k (stereo, 48 kHz).
# usage: encode.sh <rendered video> <mix.wav> <out dir> <name> [video bitrate]   e.g. encode.sh out/blazma-x2/video_16x9.mp4 blazma-x2/audio/mix.wav deliver x2-16x9 2400k
V=$1; A=$2; D=$3; N=$4; VB=${5:-2400k}; FPS=${FPS:-30}; mkdir -p "$D"; LOG=$(mktemp -u "${TMPDIR:-/tmp}/x264.XXXXXX")
VF="fps=$FPS,format=yuv420p"; X="-c:v libx264 -profile:v high -preset slow -tune film -b:v $VB -maxrate 4500k -bufsize 9000k -g $((FPS*2)) -pix_fmt yuv420p -color_primaries bt709 -color_trc bt709 -colorspace bt709"
ffmpeg -v error -y -i "$V" -vf "$VF" $X -pass 1 -passlogfile "$LOG" -an -f null /dev/null
ffmpeg -v error -y -i "$V" -i "$A" -vf "$VF" $X -pass 2 -passlogfile "$LOG" -c:a aac -b:a 128k -ar 48000 -ac 2 -shortest -movflags +faststart "$D/$N.mp4"

rm -f "$LOG"*; ls -l "$D/$N.mp4"
