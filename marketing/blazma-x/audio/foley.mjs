// Blazma X: foley. NO music. Synthesizes the film's sound effects (keyboard, mouse, whooshes, footsteps, body fall, UI ticks, done chime)
// from the hit list in assets/edit.json (seconds on the FILM clock) -> audio/sfx.wav (stereo float, 48 kHz). Seeded: same input, same file.
//   node blazma-x/audio/foley.mjs
import fs from 'node:fs';
import path from 'node:path';
const ROOT = process.cwd(), DIR = path.join(ROOT, 'blazma-x');
const edit = JSON.parse(fs.readFileSync(path.join(DIR, 'assets/edit.json'), 'utf8'));
const DUR = Number(JSON.parse(fs.readFileSync(path.join(DIR, 'film.json'), 'utf8')).duration);
const SR = 48000, TAU = Math.PI * 2;
function rng(a) { return () => { a |= 0; a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
let R = rng(1);
const N = (sec) => new Float32Array(Math.max(1, Math.round(sec * SR)));
const white = (n) => { const x = new Float32Array(n); for (let i = 0; i < n; i++) x[i] = R() * 2 - 1; return x; };
const env = (x, a, d) => { for (let i = 0; i < x.length; i++) { const t = i / SR; x[i] *= Math.min(1, t / a) * Math.exp(-t / d); } return x; };
function bp(x, f, q = 1.2) { const w = TAU * f / SR, al = Math.sin(w) / (2 * q), c = Math.cos(w), b0 = al, b2 = -al, a0 = 1 + al, a1 = -2 * c, a2 = 1 - al; let x1 = 0, x2 = 0, y1 = 0, y2 = 0; const y = new Float32Array(x.length);
  for (let i = 0; i < x.length; i++) { const o = (b0 * x[i] + b2 * x2 - a1 * y1 - a2 * y2) / a0; x2 = x1; x1 = x[i]; y2 = y1; y1 = o; y[i] = o; } return y; }
function lp(x, f) { const a = 1 - Math.exp(-TAU * f / SR); let y = 0; const o = new Float32Array(x.length); for (let i = 0; i < x.length; i++) { y += a * (x[i] - y); o[i] = y; } return o; }
function hp(x, f) { const l = lp(x, f); return x.map((v, i) => v - l[i]); }
const sine = (n, f0, f1 = f0, k = 30) => { const x = new Float32Array(n); let ph = 0; for (let i = 0; i < n; i++) { const t = i / n; const f = f1 + (f0 - f1) * Math.exp(-k * t); ph += TAU * f / SR; x[i] = Math.sin(ph); } return x; };
const add = (a, b, at = 0, g = 1) => { for (let i = 0; i < b.length; i++) if (at + i < a.length) a[at + i] += b[i] * g; return a; };
const mixTo = (len, parts) => { const o = new Float32Array(len); for (const [x, at, g] of parts) add(o, x, Math.round(at * SR), g); return o; };

// ---- voices (each returns a Float32Array; level is relative, the master sets the loudness)
function keyDown(o = {}) {                                          // mechanical-ish laptop key: bright click + body thock
  const f = o.pitch ?? 1, n = N(0.09);
  const click = env(bp(white(n.length), 3400 * f * (0.9 + 0.2 * R()), 0.9), 0.0004, 0.007);
  const thock = env(sine(n.length, 210 * f, 150 * f, 55), 0.0008, 0.016);
  const air = env(hp(white(n.length), 6000), 0.0003, 0.003);
  return mixTo(n.length, [[click, 0, 1.0], [thock, 0, 0.9], [air, 0, 0.25]]);
}
function keyUp() { const n = N(0.04); return env(bp(white(n.length), 2600, 1.0), 0.0003, 0.004).map((v) => v * 0.45); }
const key = (o) => mixTo(N(0.2).length, [[keyDown(o), 0, 1], [keyUp(), 0.075 + 0.02 * R(), 0.55]]);
const space = () => mixTo(N(0.2).length, [[keyDown({ pitch: 0.62 }), 0, 1.15], [keyUp(), 0.09, 0.5]]);
const backspace = () => mixTo(N(0.22).length, [[keyDown({ pitch: 0.8 }), 0, 1.2], [keyUp(), 0.085, 0.5]]);
function mouse(o = {}) {                                           // mouse button: sharp press, softer release
  const down = mixTo(N(0.07).length, [[env(bp(white(N(0.07).length), 2300, 1.4), 0.0003, 0.006), 0, 1.0], [env(sine(N(0.07).length, 1400, 900, 90), 0.0003, 0.007), 0, 0.55], [env(sine(N(0.07).length, 140, 100, 40), 0.0005, 0.012), 0, 0.5]]);
  const up = mixTo(N(0.05).length, [[env(bp(white(N(0.05).length), 1900, 1.4), 0.0003, 0.004), 0, 0.55], [env(sine(N(0.05).length, 1100, 800, 90), 0.0003, 0.005), 0, 0.3]]);
  return mixTo(N(0.3).length, [[down, 0, 1], [up, o.hold ?? 0.095, 0.8]]);
}
function whoosh(o = {}) {                                          // soft UI/air whoosh: band-passed noise sweeping up then away
  const len = o.len ?? 0.4, n = N(len), x = white(n.length), y = new Float32Array(n.length);
  const seg = 256; for (let i = 0; i < n.length; i += seg) { const t = i / n.length, f = 300 * 2 ** (3.2 * t ** 0.8), b = bp(x.subarray(i, Math.min(n.length, i + seg + 64)), f, 0.9); for (let j = 0; j < seg && i + j < n.length; j++) y[i + j] = b[j]; }
  for (let i = 0; i < n.length; i++) { const t = i / n.length; y[i] *= Math.sin(Math.PI * Math.min(1, t * 1.25)) ** 2 * (1 - 0.5 * t); }
  return y.map((v) => v * 2.4);
}
function step(o = {}) {                                           // a footfall on a soft floor: low thump + a little shoe noise
  const n = N(0.16), thump = env(sine(n.length, 118 * (o.pitch ?? 1), 62, 38), 0.001, 0.05), shoe = env(lp(white(n.length), 1500), 0.001, 0.022), tap = env(bp(white(n.length), 900, 1), 0.0005, 0.01);
  return mixTo(n.length, [[thump, 0, 1], [shoe, 0, 0.7], [tap, 0, 0.35]]);
}
function fall() {                                                 // body hits the ground: deep thud, a cloth/skid scrape, a smaller second bounce
  const n = N(1.5);
  const sub = env(sine(n.length, 95, 38, 14), 0.002, 0.28), body = env(lp(white(n.length), 700), 0.001, 0.09), slap = env(bp(white(n.length), 1800, 0.8), 0.0005, 0.018);
  const scrape = env(bp(white(n.length), 650, 0.6), 0.05, 0.16);
  const first = mixTo(n.length, [[sub, 0, 1.5], [body, 0, 1.1], [slap, 0, 0.55], [scrape, 0.03, 0.35]]);
  const b2 = mixTo(n.length, [[env(sine(n.length, 80, 45, 18), 0.002, 0.14), 0, 0.8], [env(lp(white(n.length), 600), 0.001, 0.05), 0, 0.6]]);
  return add(first, b2, Math.round(0.3 * SR), 0.55);
}
function tick(o = {}) {                                           // quiet UI tick / pop
  const f = o.pitch ?? 1500, n = N(0.12);
  return mixTo(n.length, [[env(sine(n.length, f, f * 0.8, 40), 0.0006, 0.028), 0, 0.8], [env(bp(white(n.length), f * 2, 1.5), 0.0003, 0.005), 0, 0.4]]);
}
function chime(o = {}) {                                          // soft "done": a major third pair with a tail
  const n = N(1.8), f = o.pitch ?? 880, out = new Float32Array(n.length);
  [[1, 1, 0.55], [1.5, 0.5, 0.4], [2.01, 0.35, 0.25], [3.0, 0.15, 0.12], [1.26, 0.45, 0.3]].forEach(([r, g, d], i) => add(out, env(sine(n.length, f * r, f * r, 0), 0.002, d), Math.round(i * 0.07 * SR * (i > 3 ? 0 : 1)), g));
  return out;
}
const V = { key, space, backspace, mouse, whoosh, step, fall, tick, chime };

// ---- place
const len = Math.round(DUR * SR), L = new Float32Array(len), Rr = new Float32Array(len), send = new Float32Array(len);
const GAIN = { key: 0.8, space: 0.85, backspace: 0.85, mouse: 0.8, whoosh: 0.5, step: 0.62, fall: 0.62, tick: 0.45, chime: 0.55 };
let placed = 0;
edit.hits.forEach(([t, label, name, o = {}], k) => {
  const v = V[name]; if (!v) throw new Error(`unknown voice ${name}`);
  R = rng(1000 + k * 977);
  const x = v(o), g = (o.gain ?? 1) * GAIN[name], pan = Math.max(-1, Math.min(1, o.pan ?? 0));
  const at = Math.round((t - (name === 'whoosh' ? (o.len ?? 0.4) * 0.62 : 0)) * SR), gl = g * Math.sqrt((1 - pan) / 2) * Math.SQRT2, gr = g * Math.sqrt((1 + pan) / 2) * Math.SQRT2, verb = o.verb ?? (name === 'fall' || name === 'chime' ? 0.25 : 0.06);
  for (let i = 0; i < x.length; i++) { const d = at + i; if (d < 0 || d >= len) continue; L[d] += gl * x[i]; Rr[d] += gr * x[i]; send[d] += verb * g * x[i]; }
  placed++;
});
// small room: three damped taps per side
const tap = (src, dst, ms, g, lpf) => { const dly = Math.round(ms * SR / 1000); const f = lp(src, lpf); for (let i = dly; i < len; i++) dst[i] += f[i - dly] * g; };
tap(send, L, 23, 0.5, 3000); tap(send, L, 61, 0.32, 2400); tap(send, L, 113, 0.18, 1800); tap(send, Rr, 31, 0.5, 3000); tap(send, Rr, 73, 0.32, 2400); tap(send, Rr, 127, 0.18, 1800);
const data = Buffer.alloc(len * 8); let peak = 0;
for (let i = 0; i < len; i++) { data.writeFloatLE(L[i], i * 8); data.writeFloatLE(Rr[i], i * 8 + 4); peak = Math.max(peak, Math.abs(L[i]), Math.abs(Rr[i])); }
const h = Buffer.alloc(44); h.write('RIFF', 0); h.writeUInt32LE(36 + data.length, 4); h.write('WAVE', 8); h.write('fmt ', 12); h.writeUInt32LE(16, 16); h.writeUInt16LE(3, 20); h.writeUInt16LE(2, 22); h.writeUInt32LE(SR, 24); h.writeUInt32LE(SR * 8, 28); h.writeUInt16LE(8, 32); h.writeUInt16LE(32, 34); h.write('data', 36); h.writeUInt32LE(data.length, 40);
fs.writeFileSync(path.join(DIR, 'audio/sfx.wav'), Buffer.concat([h, data]));
console.log(`${placed} sounds -> blazma-x/audio/sfx.wav (peak ${(20 * Math.log10(peak + 1e-9)).toFixed(1)} dBFS)`);
