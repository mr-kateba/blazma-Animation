#!/usr/bin/env bash
# usage: encode2.sh <film> <video file> <audio wav> <out> <video bitrate> [fps]
F=$1; V=$2; A=$3; O=$4; VB=$5; FPS=${6:-60}
ffmpeg -v error -y -i $V -vf fps=$FPS -c:v libx264 -preset slow -tune film -b:v $VB -maxrate 6000k -bufsize 9000k -pix_fmt yuv420p -pass 1 -passlogfile /tmp/x264b -an -f null /dev/null
ffmpeg -v error -y -i $V -i $A -vf fps=$FPS -c:v libx264 -preset slow -tune film -b:v $VB -maxrate 6000k -bufsize 9000k -pix_fmt yuv420p -pass 2 -passlogfile /tmp/x264b -c:a aac -b:a 128k -shortest -movflags +faststart $O
ls -l $O
