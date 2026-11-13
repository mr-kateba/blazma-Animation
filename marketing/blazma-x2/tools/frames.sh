#!/usr/bin/env bash
# Converts the parts of the two real screen recordings the film uses into 30 fps JPEG sequences (every second 60 fps frame).
#   d: rawx2 (UI scale 1.6 take), 14.95 s .. 19.0 s   r: rawx3 (UI scale 1.0 take), 24.3 s .. 34.7 s
set -e
R=${R:-${BLAZMA_REEL:-$HOME/blazma-reel}}; F=$R/blazma-x2/assets/seq
seq_from() { # <raw dir> <out> <first 60fps frame> <last>
  rm -rf "$F/$2"; mkdir -p "$F/$2"; local n=1
  for ((i=$3; i<=$4; i+=2)); do ffmpeg -v error -y -i "$1/f$(printf %08d $i).png" -q:v 2 "$F/$2/$(printf %05d $n).jpg"; n=$((n+1)); done
  echo "$2: $((n-1)) frames"
}
[ "$ONLY" = r ] || seq_from $R/rawx2 d 897 1141 &
seq_from $R/rawx3 r 1458 2082 &
wait
