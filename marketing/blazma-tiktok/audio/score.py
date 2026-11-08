# /// script
# requires-python = ">=3.10"
# dependencies = ["librosa>=0.10", "numpy", "scipy", "soundfile"]
# ///
"""TikTok cut of the Blazma score: 37.5 s, 128 bpm, 80 beats, D Hijaz (same instruments as the explainer's score.py).

    MOTION_REEL_SKILL=<skill> uv run <film>/audio/score.py

 0-6   studio sting (Studio Nakhla slot): ney + oud taqsim, riser, darbuka roll, a breath of silence
 6     DROP into the hook      6-24 groove, oud melody, qanun from 14
 24    fall hit + sub, 24-28 halftime
 28    lift                    28-66 feature run, fills into every cut
 66-72 halftime (slow-motion button)
 72    end-card hit, D major resolve, ring out
Writes <film>/audio/music.wav and <film>/beats.json.
"""
import importlib.util
import json
import sys
from pathlib import Path

import numpy as np
import soundfile as sf
from scipy import signal

HERE = Path(__file__).resolve().parent
FILM = HERE.parent
spec = importlib.util.spec_from_file_location("expl", FILM.parent / "blazma-explainer/audio/score.py")
E = importlib.util.module_from_spec(spec)
spec.loader.exec_module(E)            # only definitions (instruments, phrases); its main() is not run
from dsp import *  # noqa: E402,F403
import dsp  # noqa: E402

BPM, NB = 128, 82
OFF = 2          # the studio intro (Nakhla, own footage + audio) takes beats 0-8; the old 0-6 sting + hook shift by 2 beats
PB = 80          # beats of the original arrangement (before the shift)
BEAT = 60 / BPM
DUR = NB * BEAT
N = int(SR * DUR)
T = lambda b: b * BEAT  # noqa: E731
CUTS = [28, 36, 46, 56, 66, 72]
oud, qanun, ney, dum, tek, ka = E.oud, E.qanun, E.ney, E.dum, E.tek, E.ka
ROOTS, CH, ARP, PH_A, PH_B, TAQSIM = E.ROOTS, E.CH, E.ARP, E.PH_A, E.PH_B, E.TAQSIM


def region(b):
    if b < 6: return "intro"
    if 24.25 <= b < 28 or 66 <= b < 72: return "half"
    if b >= 72: return "end"
    return "full"


def main():
    class SBus(Bus):                      # everything after the intro is placed OFF beats later
        def add(self, sig, beat, gain=1.0, pan=0.0, at=None):
            super().add(sig, beat + (OFF if at is None else 0), gain, pan, at)
    drums, perc, bass, lead, pad, fx = (SBus(DUR, BPM) for _ in range(6))
    kick_t = []

    def K(b, g=0.95):
        drums.add(kick(), b, g); kick_t.append(T(b + OFF))

    # ---- studio sting 0-6
    A = lambda b: dict(at=T(b))           # absolute placement (not shifted)
    pad.add(pad_chord(["D3", "A3", "D4"], T(8), (450, 2400), attack=2.4, release=0.1), 0, 0.26, **A(0))
    lead.add(ney("A4", 4.0, 0.7), 0.6, 0.26, pan=0.15, **A(0.6))
    lead.add(ney("D5", 3.0, 0.7), 4.8, 0.22, pan=0.15, **A(4.8))
    fx.add(riser(T(4.5)), 3.5, 0.40, **A(3.5))
    perc.add(dum(), 4, 0.35, **A(4)); perc.add(dum(), 6, 0.42, **A(6))
    for k, b in enumerate(np.arange(6.5, 7.75, 0.25)):
        (perc.add(tek(), b, 0.2 + 0.05 * k, pan=0.1, **A(b)) if k % 2 == 0 else perc.add(ka(), b, 0.2 + 0.05 * k, pan=-0.1, **A(b)))
    # ---- bars
    for b in range(6, PB):
        pos, root, rg = b % 4, ROOTS[(b // 4) % 4], region(b)
        if rg == "full":
            if b != 23: K(b)
            if pos in (1, 3): drums.add(clap(), b, 0.42, pan=0.05)
            drums.add(hat(), b + 0.5, 0.2, pan=0.25)
            if b >= 14: drums.add(hat(), b + 0.75, 0.08, pan=-0.3)
            if pos == 0:
                for off, f, g in ((0, dum, .62), (.5, tek, .34), (1.5, tek, .32), (2, dum, .55), (2.5, ka, .18), (3, tek, .36), (3.5, ka, .2)):
                    perc.add(f(), b + off, g, pan=-0.15 if f is dum else 0.2)
            if b != 23:
                for h in (0, 1):
                    bass.add(bass_note(root if h == 0 else root[:-1] + "3", BEAT * 0.45, 900), b + h * 0.5, 0.55 if h == 0 else 0.4)
            if 14 <= b < 24 or 28 <= b < 66:
                for k in range(4):
                    lead.add(qanun(ARP[root][k]), b + k * 0.25, 0.13, pan=(-0.35 if k % 2 else 0.35))
        elif rg == "half":
            if pos in (0, 2): K(b, 0.95); perc.add(dum(), b, 0.5, pan=-0.1); bass.add(bass_note(root, BEAT * 1.8, 700), b, 0.6)
            if pos == 2: drums.add(clap(), b, 0.45)
            if pos in (1, 3): drums.add(hat(True), b + 0.5, 0.12, pan=0.2)
        elif rg == "end" and b == 72:
            K(b, 1.0)
        elif rg == "end" and b == 76:
            perc.add(dum(), b, 0.4)
        if pos == 0 and 6 <= b < 72:
            pad.add(pad_chord(CH[root], T(3.9), (900, 3200), attack=0.4, release=0.5), b, 0.15)

    def phrase(ph, b0, g=0.5, pan=-0.1, octave=0):
        for off, nt, ln in ph:
            if octave: nt = nt[:-1] + str(int(nt[-1]) + octave)
            lead.add(oud(nt, ln), b0 + off, g, pan=pan)
    phrase(PH_A, 6, 0.55); phrase(PH_B, 14, 0.55)
    for i, b0 in enumerate(range(28, 66, 8)):
        phrase(PH_A if i % 2 == 0 else PH_B, b0, 0.5)
    for b0, nt in ((24.5, "A4"), (26.5, "F#4")):
        lead.add(ney(nt, 1.9, 0.7), b0, 0.32); lead.add(oud("D4", 2.0), b0, 0.35, pan=-0.1)
    for b0, nt in ((66, "D5"), (69, "A4")):
        lead.add(ney(nt, 2.9, 0.8), b0, 0.33, pan=0.1); lead.add(oud("D4", 2.8), b0, 0.34, pan=-0.1)
    for b0 in (28, 44):
        for off, nt, ln in ((0, "F#5", 3.5), (4, "G5", 3.5), (8, "F#5", 2), (10, "Eb5", 2), (12, "D5", 4)):
            lead.add(ney(nt, ln, 0.5), b0 + off, 0.2, pan=0.2)
    # ---- hits and fills
    K(6, 1.0); fx.add(crash(1.7), 6, 0.32); fx.add(sub_drop(1.6, 74, 30), 6, 0.8); fx.add(whoosh(T(1.2), 300, 5000, 0.9), 4.8, 0.2)
    K(24, 1.0); fx.add(crash(1.9), 24, 0.36); fx.add(sub_drop(1.8, 66, 26), 24, 1.0); perc.add(dum(), 24, 0.7); fx.add(riser(T(2.4)), 21.6, 0.3)
    fx.add(crash(1.5), 28, 0.28); fx.add(sub_drop(1.0), 28, 0.55); fx.add(riser(T(2)), 26, 0.22)
    fx.add(crash(3.2), 72, 0.4); fx.add(sub_drop(2.4, 70, 28), 72, 0.9)
    for c in CUTS[:-1]:
        for k, b in enumerate(np.arange(c - 1, c - 0.01, 0.25)):
            (perc.add(tek(), b, 0.3, pan=0.1) if k % 2 == 0 else perc.add(ka(), b, 0.28, pan=-0.1))
    for k, nt in enumerate(["D3", "A3", "D4", "F#4", "A4", "D5"]):
        lead.add(oud(nt, 3.5), 72.05 + k * 0.06, 0.5, pan=-0.3 + 0.12 * k)
    lead.add(ney("A5", 7.0, 0.5), 72.5, 0.30, pan=0.15)
    pad.add(pad_chord(["D3", "A3", "D4", "F#4", "A4"], T(7.6), (1600, 5200), attack=0.15, release=2.0), 72, 0.5)
    for k, nt in enumerate(["D5", "F#5", "A5", "D6"]):
        lead.add(qanun(nt, 1.2), 76 + k * 0.5, 0.18, pan=0.3 - 0.2 * k)

    pump = sidechain(kick_t, bass.x.shape[1], 0.5, 0.11)
    ir = reverb_ir(1.7, 0.4)
    wet_src = lead.x * 0.4 + perc.x * 0.12 + fx.x * 0.1 + pad.x * 0.2
    wet = np.stack([signal.fftconvolve(wet_src[c], ir[c])[: wet_src.shape[1]] for c in range(2)])
    bed = drums.x * 0.8 + perc.x * 0.85 + bass.x * pump * 0.8 + lead.x * (0.55 + 0.4 * pump) * 0.95 + pad.x * (0.5 + 0.5 * pump) + fx.x * 0.7 + wet * 0.5
    bed = np.stack([filt(ch, "highpass", 28) for ch in bed])[:, :N]
    g = np.ones(N)
    for a, b in ((5.8 + OFF, 5.995 + OFF), (23.8 + OFF, 23.995 + OFF)):
        i0, i1 = int(T(a) * SR), int(T(b) * SR); g[i0:i1] = np.linspace(1, 0.0, i1 - i0) ** 0.5
    bed *= g
    ia, isr = sf.read(HERE.parent / 'assets/nakhla/intro-audio.wav')
    ia = ia.T[:, : int(3.5 * SR)] if ia.ndim > 1 else np.stack([ia, ia])[:, : int(3.5 * SR)]
    ia = ia / (np.abs(ia).max() + 1e-9) * 0.55
    fd = int(0.45 * SR); ia[:, -fd:] *= np.linspace(1, 0, fd) ** 2
    bed[:, : ia.shape[1]] += ia   # the studio's own intro audio, blended into the opening
    bed /= np.abs(bed).max() + 1e-9
    bed *= 0.5
    fade = int(0.4 * SR); bed[:, -fade:] *= np.linspace(1, 0, fade) ** 2
    sf.write(HERE / "music.wav", bed.T, SR, subtype="PCM_24")
    grid = measure_beats((drums.x + perc.x * 0.6)[:, :N].mean(axis=0), SR, BPM, n_beats=NB + 1)
    mono = bed.mean(axis=0)
    per_bar = [float(np.sqrt(np.mean(mono[int(T(4 * i) * SR):int(T(4 * i + 4) * SR)] ** 2))) for i in range(NB // 4)]
    mx = max(per_bar)
    grid.update({"downbeats": grid["beats"][::4], "bar_energy": [round(x / mx, 4) for x in per_bar], "duration": DUR,
                 "sections": [{"name": "intro", "beat": 0}, {"name": "main", "beat": 8}, {"name": "half", "beat": 26}, {"name": "lift", "beat": 30},
                              {"name": "half2", "beat": 68}, {"name": "end", "beat": 74}],
                 "designed_bpm": BPM, "source": "blazma-tiktok/audio/score.py (explainer instruments, 80-beat cut)"})
    (FILM / "beats.json").write_text(json.dumps(grid, indent=1))
    print(f"beats: {grid['bpm']} bpm, offset {grid['offset'] * 1000:.1f} ms, {grid['fit_inliers']}/{len(grid['detected'])} inliers, rms {grid['fit_residual_ms']} ms")
    print("loudness:", dsp.ebur128(HERE / "music.wav"))


if __name__ == "__main__":
    main()
