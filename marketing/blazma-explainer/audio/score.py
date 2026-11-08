# /// script
# requires-python = ">=3.10"
# dependencies = ["librosa>=0.10", "numpy", "scipy", "soundfile"]
# ///
"""Bespoke score for the Blazma Animation explainer: 60 s, 128 bpm, 128 beats, D Hijaz.

    MOTION_REEL_SKILL=<skill> uv run <film>/audio/score.py

Cinematic electronic with an Arabic flavour. Synthesized oud (Karplus-Strong pluck, mid register melody),
qanun (bright pluck arpeggios), ney (breathy sine-harmonic lead with vibrato), darbuka (dum / tek / ka, maqsum
pattern) layered with a modern kick + sub, hats and claps. Form follows docs/shotlist.md (beats):
  0-12   intro: ney + pad drone, oud taqsim, darbuka entering, riser + darbuka roll into the drop
  12     DROP (big hit)        12-25  groove, oud melody A/B, qanun arps from 20
  25.25  hero fall hit; 26-30  halftime
  30     lift: crash, full band, qanun arps, ney      30-86  feature run (fills into every cut)
  86-98  breakdown (Claude / MCP): no kick, pad + qanun arpeggio + soft darbuka, riser into 98
  98-108 full groove ("and more")   108-116 halftime (slow-motion fall)   116 end-card hit, D major resolve
Writes <film>/audio/music.wav (+ stems/) and <film>/beats.json.
"""
import json
import os
import sys
from pathlib import Path

import librosa
import numpy as np
import soundfile as sf
from scipy import signal


def _skill_dir():
    if os.environ.get("MOTION_REEL_SKILL"):
        return Path(os.environ["MOTION_REEL_SKILL"])
    sys.exit("set MOTION_REEL_SKILL to the folder that holds the motion-reel toolkit (scripts/dsp.py)")


sys.path.insert(0, str(_skill_dir() / "scripts"))
from dsp import *  # noqa: E402,F403
import dsp  # noqa: E402

HERE = Path(__file__).resolve().parent
FILM = HERE.parent
BPM, NB = 128, 128
BEAT = 60 / BPM
DUR = NB * BEAT                      # 60.0 s
N = int(SR * DUR)
seed(20261107)
T = lambda b: b * BEAT  # noqa: E731

# ---------------------------------------------------------------- instruments
_cache = {}


def cached(key, fn):
    if key not in _cache:
        _cache[key] = fn()
    return _cache[key]


def oud(note, beats=0.5):
    L = min(max(beats * BEAT * 1.3, 0.25), 1.6)
    def mk():
        x = ks_pluck(hz(note), L, bright=0.5, buzz=0.1)
        return peq(filt(x, "lowpass", 4200, 2), 280, 3.0, 0.9)    # woody body
    return cached(("oud", note, round(L, 2)), mk)


def qanun(note, L=0.32):
    return cached(("qan", note, L), lambda: filt(ks_pluck(hz(note), L, bright=0.9, buzz=0.0), "highpass", 250, 2))


def ney(note, beats=2.0, breath=0.5):
    n = int(beats * BEAT * SR)
    t = tvec(n)
    f = hz(note)
    vib = 1 + 0.004 * np.sin(2 * np.pi * 5.4 * t) * np.clip(t / 0.5, 0, 1)
    ph = np.cumsum(f * vib) / SR * 2 * np.pi
    x = np.sin(ph) + 0.32 * np.sin(2 * ph) + 0.1 * np.sin(3 * ph)
    air = filt(rng.standard_normal(n), "bandpass", [f * 1.8, f * 4.5], 2) * (0.35 * breath)
    x = (x + air * (0.4 + 0.6 * np.clip(1 - t / 0.25, 0, 1) + 0.4)) * adsr(n, 0.09, 0.2, 0.8, 0.28)
    return filt(x, "lowpass", 5200, 2) * 0.7


def dum():
    n = int(0.42 * SR); t = tvec(n)
    f = 72 + 110 * np.exp(-t / 0.03)
    x = np.sin(2 * np.pi * np.cumsum(f) / SR) * np.exp(-t / 0.13)
    x += filt(rng.standard_normal(n) * np.exp(-t / 0.004), "lowpass", 1400) * 0.5
    return np.tanh(1.5 * x) * (1 - np.exp(-t / 0.001))


def tek(bright=1.0):
    n = int(0.16 * SR); t = tvec(n)
    x = filt(rng.standard_normal(n), "bandpass", [2400, 7500]) * np.exp(-t / 0.016) * 0.9
    x += np.sin(2 * np.pi * 980 * t * (1 + 0.05 * np.exp(-t / 0.01))) * np.exp(-t / 0.018) * 0.3
    return x * bright * (1 - np.exp(-t / 0.0005))


def ka():
    n = int(0.12 * SR); t = tvec(n)
    x = filt(rng.standard_normal(n), "bandpass", [900, 3200]) * np.exp(-t / 0.018) * 0.7
    x += np.sin(2 * np.pi * 420 * t) * np.exp(-t / 0.02) * 0.25
    return x * (1 - np.exp(-t / 0.0005))


# ---------------------------------------------------------------- score data
ROOTS = ["D2", "D2", "Eb2", "D2"]                    # the Hijaz vamp: i, i, bII, i (a bar each)
CH = {"D2": ["D3", "F#3", "A3", "D4"], "Eb2": ["Eb3", "G3", "Bb3", "Eb4"]}
ARP = {"D2": ["D5", "F#5", "A5", "F#5"], "Eb2": ["Eb5", "G5", "Bb5", "G5"]}
PH_A = [(0, "A4", 1.0), (1, "Bb4", .5), (1.5, "A4", .5), (2, "G4", .5), (2.5, "F#4", .5), (3, "G4", 1), (4, "A4", .75),
        (4.75, "Bb4", .25), (5, "C5", .5), (5.5, "Bb4", .5), (6, "A4", 1), (7, "G4", .5), (7.5, "F#4", .5)]
PH_B = [(0, "D5", 1), (1, "C5", .5), (1.5, "Bb4", .5), (2, "A4", 1), (3, "Bb4", .5), (3.5, "A4", .5), (4, "G4", .5),
        (4.5, "F#4", .5), (5, "Eb4", 1), (6, "F#4", .5), (6.5, "G4", .5), (7, "A4", 1)]
TAQSIM = [(0, "D4", 2.0), (2, "Eb4", .5), (2.5, "F#4", .5), (3, "G4", 1.0), (4, "F#4", .5), (4.5, "Eb4", .5),
          (5, "D4", 2.0), (7, "Eb4", .5), (7.5, "D4", .5), (8, "A4", 1.5), (9.5, "Bb4", .5), (10, "A4", .5),
          (10.5, "G4", .5), (11, "F#4", .5), (11.5, "Eb4", .5)]
CUTS = [30, 40, 50, 62, 76, 86, 98, 108]


def region(b):
    if b < 12: return "intro"
    if 25.25 <= b < 30 or 108 <= b < 116: return "half"
    if 86 <= b < 98: return "break"
    if b >= 116: return "end"
    return "full"


def main():
    drums, perc, bass, lead, pad, fx = (Bus(DUR, BPM) for _ in range(6))
    kick_t = []

    def K(b, g=0.95):
        drums.add(kick(), b, g); kick_t.append(T(b))

    # ---- intro 0-12: ney + pad drone, oud taqsim, darbuka entering, riser + roll into the drop
    pad.add(pad_chord(["D3", "A3", "D4"], T(12), (450, 2600), attack=2.5, release=0.1), 0, 0.30)
    lead.add(ney("A4", 5.5, 0.7), 0.5, 0.30, pan=0.15)
    lead.add(ney("D5", 5.0, 0.7), 6.0, 0.28, pan=0.15)
    for off, nt, ln in TAQSIM:
        lead.add(oud(nt, ln), off, 0.5, pan=-0.12)
    fx.add(riser(T(7.5)), 4.5, 0.42)
    for b in (4, 6, 8):
        perc.add(dum(), b, 0.45, pan=-0.1)
    for b in (5, 7, 9):
        perc.add(tek(0.7), b, 0.28, pan=0.15)
    for k, b in enumerate(np.arange(10, 11.5, 0.25)):                # roll: 16ths crescendo
        (perc.add(tek(), b, 0.18 + 0.05 * k, pan=0.1) if k % 2 == 0 else perc.add(ka(), b, 0.18 + 0.05 * k, pan=-0.1))
    for k, b in enumerate(np.arange(11.5, 11.75, 0.125)):
        perc.add(tek(), b, 0.45, pan=0.1)

    # ---- the bars
    for b in range(12, NB):
        bar, pos = (b - 12) // 4 if b >= 12 else 0, b % 4
        root = ROOTS[(b // 4) % 4]
        rg = region(b)
        if rg in ("full", "half", "end"):
            pass
        # kick + clap + hats
        if rg == "full":
            if not (b == 25): K(b)
            if pos in (1, 3): drums.add(clap(), b, 0.42, pan=0.05)
            drums.add(hat(), b + 0.5, 0.2, pan=0.25)
            if b >= 30: drums.add(hat(), b + 0.75, 0.08, pan=-0.3)
        elif rg == "half":
            if pos in (0, 2): K(b, 0.95)
            if pos == 2 or (b == 26): drums.add(clap(), b, 0.45)
            if pos in (1, 3): drums.add(hat(True), b + 0.5, 0.12, pan=0.2)
        elif rg == "break":
            if pos == 0: drums.add(kick(soft=True), b, 0.35)
            drums.add(hat(), b + 0.5, 0.09, pan=0.25)
        elif rg == "end" and b == 116:
            K(b, 1.0)
        elif rg == "end" and b in (120,):
            perc.add(dum(), b, 0.4)
        # darbuka (maqsum): dum tek _ tek dum _ tek _
        if rg == "full":
            for off, f, g in ((0, dum, .8), (.5, tek, .42), (1.5, tek, .4), (2, dum, .7), (2.5, ka, .22), (3, tek, .45), (3.5, ka, .25)):
                if pos == 0 or True:
                    pass
            if pos == 0:
                for off, f, g in ((0, dum, .62), (.5, tek, .34), (1.5, tek, .32), (2, dum, .55), (2.5, ka, .18), (3, tek, .36), (3.5, ka, .2)):
                    perc.add(f(), b + off, g, pan=-0.15 if f is dum else 0.2)
        elif rg == "half" and pos in (0, 2):
            perc.add(dum(), b, 0.5, pan=-0.1)
            perc.add(tek(), b + 1.5, 0.3, pan=0.2)
        elif rg == "break":
            if pos == 0:
                perc.add(dum(), b, 0.35); perc.add(tek(0.6), b + 3, 0.26, pan=0.2); perc.add(dum(), b + 2, 0.28)
        # bass: eighths on the root, octave bounce (not in break/end)
        if rg in ("full",) and not (b == 25):
            for h in (0, 1):
                nt = root if h == 0 else root[:-1] + "3"
                bass.add(bass_note(nt, BEAT * 0.45, 900), b + h * 0.5, 0.55 if h == 0 else 0.4)
        elif rg == "half" and pos in (0, 2):
            bass.add(bass_note(root, BEAT * 1.8, 700), b, 0.6)
        elif rg == "break" and pos == 0:
            bass.add(bass_note("D2", BEAT * 3.6, 500), b, 0.45)
        # pad bar chords (one per bar, on the bar's first beat)
        if pos == 0 and 12 <= b < 116:
            g = 0.30 if rg == "break" else 0.15
            pad.add(pad_chord(CH[root], T(3.9), (900, 3200) if rg != "break" else (500, 1800), attack=0.4, release=0.5), b, g)
        # qanun arpeggios: 16ths from beat 20, through the feature run, return and break (8ths)
        if (20 <= b < 25 or 30 <= b < 86 or 98 <= b < 108) and rg == "full":
            for k in range(4):
                lead.add(qanun(ARP[root][k]), b + k * 0.25, 0.13, pan=(-0.35 if k % 2 else 0.35))
        elif rg == "break":
            for k in range(4):
                lead.add(qanun(ARP[root][k], 0.5), b + k * 0.5 - 0.0, 0.17, pan=(-0.4 if k % 2 else 0.4))

    # ---- melody (oud) over the groove, ney sustains, halftime sustains
    def phrase(ph, b0, g=0.5, pan=-0.1, octave=0):
        for off, nt, ln in ph:
            if octave: nt = nt[:-1] + str(int(nt[-1]) + octave)
            lead.add(oud(nt, ln), b0 + off, g, pan=pan)
    for i, b0 in enumerate(range(12, 25, 8)):
        phrase(PH_A if i % 2 == 0 else PH_B, b0, 0.55)
    phrase(PH_B[:6], 20 if False else 20, 0.0)                       # (keeps the 12-28 form: A 12-20, B 20-25)
    for b0 in range(30, 86, 8):
        phrase(PH_A if (b0 // 8) % 2 == 0 else PH_B, b0, 0.5, octave=0)
    for b0 in (98, 106):
        phrase(PH_A, b0, 0.55, octave=1)
    for b0, nt in ((26, "A4"), (28, "F#4")):
        lead.add(ney(nt, 1.9, 0.7), b0, 0.32); lead.add(oud("D4", 2.0), b0, 0.35, pan=-0.1)
    for b0, nt in ((108, "D5"), (112, "A4")):
        lead.add(ney(nt, 3.9, 0.8), b0, 0.33, pan=0.1)
        lead.add(oud("D4", 3.5), b0, 0.34, pan=-0.1)
    for b0 in range(30, 62, 16):                                       # ney line over the lift
        for off, nt, ln in ((0, "F#5", 3.5), (4, "G5", 3.5), (8, "F#5", 2), (10, "Eb5", 2), (12, "D5", 4)):
            lead.add(ney(nt, ln, 0.5), b0 + off, 0.2, pan=0.2)
    for b0 in (98,):
        for off, nt, ln in ((0, "A5", 3.5), (4, "Bb5", 3.5)):
            lead.add(ney(nt, ln, 0.5), b0 + off, 0.2, pan=0.2)
    # breakdown: ney tune
    for b0, nt, ln in ((86, "A4", 3.5), (90, "G4", 1.5), (91.5, "F#4", 0.5), (92, "Eb4", 1.5), (94, "D4", 3.8)):
        lead.add(ney(nt, ln, 0.9), b0, 0.34, pan=0.1)

    # ---- hits and fills
    drums.add(kick(), 12, 1.0); kick_t.append(T(12))
    fx.add(crash(1.7), 12, 0.32); fx.add(sub_drop(1.6, 74, 30), 12, 0.8)
    fx.add(whoosh(T(1.5), 300, 5000, 0.9), 10.5, 0.2)
    # hero fall: pre-hit silence is the gap in the groove; big hit + sub on 25.25
    K(25.25, 1.0); fx.add(crash(1.9), 25.25, 0.36); fx.add(sub_drop(1.8, 66, 26), 25.25, 1.0)
    perc.add(dum(), 25.25, 0.7)
    fx.add(riser(T(2.4)), 22.85, 0.3)
    # the lift (30), return (98), end card (116)
    fx.add(crash(1.5), 30, 0.28); fx.add(sub_drop(1.0), 30, 0.55)
    fx.add(riser(T(2)), 28, 0.22)
    fx.add(crash(1.5), 98, 0.28); fx.add(sub_drop(1.0), 98, 0.6)
    fx.add(riser(T(4)), 94, 0.35)
    fx.add(riser(T(1.8)), 106.2, 0.25)
    K(98, 1.0)
    fx.add(crash(3.2), 116, 0.4); fx.add(sub_drop(2.4, 70, 28), 116, 0.9)
    for c in CUTS[:-2]:                                              # fills into each cut
        for k, b in enumerate(np.arange(c - 1, c - 0.01, 0.25)):
            (perc.add(tek(), b, 0.3, pan=0.1) if k % 2 == 0 else perc.add(ka(), b, 0.28, pan=-0.1))
    # end card: D major strum (oud) + ney + wide pad, ringing out
    for k, nt in enumerate(["D3", "A3", "D4", "F#4", "A4", "D5"]):
        lead.add(oud(nt, 3.5), 116.05 + k * 0.06, 0.5, pan=-0.3 + 0.12 * k)
    lead.add(ney("A5", 7.5, 0.5), 116.5, 0.30, pan=0.15)
    pad.add(pad_chord(["D3", "A3", "D4", "F#4", "A4"], T(11.5), (1600, 5200), attack=0.15, release=2.0), 116, 0.5)
    for k, nt in enumerate(["D5", "F#5", "A5", "D6"]):
        lead.add(qanun(nt, 1.2), 120 + k * 0.5, 0.18, pan=0.3 - 0.2 * k)

    # ---- bed: kick pump, reverb, gaps, fade
    pump = sidechain(kick_t, bass.x.shape[1], 0.5, 0.11)
    ir = reverb_ir(1.7, 0.4)
    wet_src = lead.x * 0.4 + perc.x * 0.12 + fx.x * 0.1 + pad.x * 0.2
    wet = np.stack([signal.fftconvolve(wet_src[c], ir[c])[: wet_src.shape[1]] for c in range(2)])
    bed = drums.x * 0.8 + perc.x * 0.85 + bass.x * pump * 0.8 + lead.x * (0.55 + 0.4 * pump) * 0.95 + pad.x * (0.5 + 0.5 * pump) + fx.x * 0.7 + wet * 0.5
    bed = np.stack([filt(ch, "highpass", 28) for ch in bed])[:, :N]
    g = np.ones(N)                                                   # two micro-gaps: before the drop and before the fall hit
    for a, b in ((11.8, 11.995), (25.0, 25.245)):
        i0, i1 = int(T(a) * SR), int(T(b) * SR)
        g[i0:i1] = np.linspace(1, 0.0, i1 - i0) ** 0.5
    bed *= g
    bed /= np.abs(bed).max() + 1e-9
    bed *= 0.5
    fade = int(0.4 * SR); bed[:, -fade:] *= np.linspace(1, 0, fade) ** 2
    sf.write(HERE / "music.wav", bed.T, SR, subtype="PCM_24")
    (HERE / "stems").mkdir(exist_ok=True)
    for nme, bus in (("drums", drums), ("perc", perc), ("bass", bass), ("lead", lead), ("pad", pad), ("fx", fx)):
        sf.write(HERE / "stems" / f"{nme}.wav", (bus.x[:, :N] * 0.5).T, SR, subtype="PCM_16")

    grid_src = (drums.x + perc.x * 0.6)[:, :N].mean(axis=0)
    grid = measure_beats(grid_src, SR, BPM, n_beats=NB + 1)
    mono = bed.mean(axis=0)
    per_bar = [float(np.sqrt(np.mean(mono[int(T(4 * i) * SR):int(T(4 * i + 4) * SR)] ** 2))) for i in range(NB // 4)]
    mx = max(per_bar)
    grid.update({"downbeats": grid["beats"][::4], "bar_energy": [round(x / mx, 4) for x in per_bar], "duration": DUR,
                 "sections": [{"name": "intro", "beat": 0}, {"name": "main", "beat": 12}, {"name": "half", "beat": 26},
                              {"name": "lift", "beat": 30}, {"name": "break", "beat": 86}, {"name": "return", "beat": 98},
                              {"name": "end", "beat": 116}],
                 "designed_bpm": BPM, "source": "blazma-explainer/audio/score.py: oud/qanun/ney/darbuka + kick, drum stems, librosa + linear fit"})
    (FILM / "beats.json").write_text(json.dumps(grid, indent=1))
    print(f"beats: {grid['bpm']} bpm, offset {grid['offset'] * 1000:.1f} ms, {grid['fit_inliers']}/{len(grid['detected'])} inliers, rms {grid['fit_residual_ms']} ms")
    print("loudness:", dsp.ebur128(HERE / "music.wav"))


if __name__ == "__main__":
    main()
