// Blazma X: audio master. NO music. Stems: the Studio Nakhla intro's own sound effect (first 3.5 s) + the film's UI/diegetic SFX (sfx.wav from sfx.mjs).
// Masters to about -16 LUFS integrated (quiet, fine with sound on), true peak <= -1.5 dBTP.
//   node blazma-x/audio/master.mjs            -> blazma-x/audio/mix.wav
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
const ROOT = process.cwd(), A = path.join(ROOT, 'blazma-x', 'audio');
const cfg = JSON.parse(fs.readFileSync(path.join(ROOT, 'blazma-x', 'film.json'), 'utf8'));
const DUR = Number(cfg.duration), TARGET = -16, CEIL = -2.3;
const INTRO_DB = Number(process.env.INTRO_DB ?? 9);          // the studio's own sound is recorded very low (-36 LUFS): bring it up to sit with the SFX
const ff = (a) => { const r = spawnSync('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-y', ...a], { encoding: 'utf8' }); if (r.status) throw new Error(r.stderr); };
const loud = (f) => { const r = spawnSync('ffmpeg', ['-hide_banner', '-nostats', '-i', f, '-af', 'ebur128=peak=true', '-f', 'null', '-'], { encoding: 'utf8', maxBuffer: 1 << 26 });
  const s = r.stderr.slice(r.stderr.lastIndexOf('Summary:')); return { I: +/I:\s+(-?[\d.]+) LUFS/.exec(s)[1], TP: +/Peak:\s+(-?[\d.]+) dBFS/.exec(s)[1] }; };
const intro = path.join(ROOT, 'blazma-x/assets/nakhla/intro-audio.wav'), sfx = path.join(A, 'sfx.wav');
const pre = path.join(A, '_pre.wav');
const g = `[0:a]aresample=48000,aformat=channel_layouts=stereo,afade=t=out:st=3.15:d=0.35,volume=${INTRO_DB}dB,apad=whole_dur=${DUR}[i];` +
  `[1:a]aresample=48000,aformat=channel_layouts=stereo,apad=whole_dur=${DUR}[s];[i][s]amix=inputs=2:normalize=0:duration=longest,atrim=0:${DUR},afade=t=out:st=${DUR - 0.25}:d=0.25[o]`;
ff(['-i', intro, '-i', sfx, '-filter_complex', g, '-map', '[o]', '-c:a', 'pcm_f32le', pre]);
const out = path.join(A, 'mix.wav'); let gain = TARGET - loud(pre).I, m;
for (let i = 0; i < 6; i++) {
  ff(['-i', pre, '-af', `volume=${gain.toFixed(3)}dB,aresample=192000,alimiter=limit=${10 ** (CEIL / 20)}:attack=1:release=80:level=0:asc=1:latency=1,aresample=48000`, '-c:a', 'pcm_s24le', '-ar', '48000', out]);
  m = loud(out); if (Math.abs(m.I - TARGET) <= 0.15) break; gain += TARGET - m.I;
}
fs.rmSync(pre);
console.log(`master: ${m.I.toFixed(1)} LUFS integrated, true peak ${m.TP.toFixed(1)} dBTP (intro +${INTRO_DB} dB, no music) -> ${path.relative(ROOT, out)}`);
