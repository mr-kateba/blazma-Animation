#!/usr/bin/env python3
"""Reads the screen-recording log + frames and writes assets/rec.json (marks in recording seconds, keystroke times).
usage: analyze.py <rec-log> <raw-frames-dir> <out-json>"""
import json, re, sys, glob, numpy as np
from PIL import Image
log, raw, out = sys.argv[1:4]
FPS = 60
marks = {}
clicks = []
for l in open(log, errors='ignore'):
    m = re.match(r'DEMO MARK (\S+) frame=(\d+)', l)
    if m:
        k, f = m.group(1), int(m.group(2))
        if k == 'click': clicks.append(f / FPS)
        else: marks[k] = f / FPS
    m = re.match(r'DEMO pointer (\d+),(\d+)', l)
    if m: marks.setdefault('pointers', []).append([int(m.group(1)), int(m.group(2))])
marks['clicks'] = clicks
files = sorted(glob.glob(raw + '/f*.png')); n = len(files)
# keystrokes: frames where the prompt textbox pixels change while typing
ts, te = int(marks['typing-start'] * FPS), int(marks['typing-end'] * FPS) + 3
box = (500, 350, 1260, 470)
prev = None; keys = []
for i in range(max(0, ts - 3), min(n, te)):
    a = np.asarray(Image.open(files[i]).convert('L').crop(box), dtype=np.int16)
    if prev is not None and np.abs(a - prev).mean() > 0.25: keys.append(round(i / FPS, 3))
    prev = a
marks['keys'] = keys
marks['frames'] = n
json.dump(marks, open(out, 'w'), indent=1)
print(json.dumps({k: (v if k not in ('keys', 'pointers') else len(v)) for k, v in marks.items()}))
