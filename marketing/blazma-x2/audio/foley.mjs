// Blazma X2: sound effects. NO music, NO voice. Few, soft, low-passed sounds on the picture's key moments only
// (keyframe set, cups set down, the collapse, the words, the move, the fall, the end), synthesized in code, seeded.
// Reads assets/edit.json "hits" (film seconds) -> audio/sfx.wav (stereo float, 48 kHz).
//   node blazma-x2/audio/foley.mjs
import fs from 'node:fs';
import path from 'node:path';
const DIR = path.join(process.cwd(), 'blazma-x2');
const edit = JSON.parse(fs.readFileSync(path.join(DIR, 'assets/edit.json'), 'utf8'));
const DUR = edit.duration, SR = 48000, TAU = Math.PI * 2;
function rng(a) { return () => { a |= 0; a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
let R = rng(1);
const N = (sec) => Math.max(1, Math.round(sec * SR));
const white = (n) => { const x = new Float32Array(n); for (let i = 0; i < n; i++) x[i] = R() * 2 - 1; return x; };
// envelope: attack a (s, raised-cosine), exponential decay d (s)
const env = (x, a, d) => { for (let i = 0; i < x.length; i++) { const t = i / SR; x[i] *= (t < a ? 0.5 - 0.5 * Math.cos(Math.PI * t / a) : 1) * Math.exp(-Math.max(0, t - a) / d); } return x; };
function lp(x, f, poles = 2) { let o = x; for (let k = 0; k < poles; k++) { const a = 1 - Math.exp(-TAU * f / SR); let y = 0; const out = new Float32Array(o.length); for (let i = 0; i < o.length; i++) { y += a * (o[i] - y); out[i] = y; } o = out; } return o; }
function lpSweep(x, fAt) { let y1 = 0, y2 = 0; const o = new Float32Array(x.length); for (let i = 0; i < x.length; i++) { const a = 1 - Math.exp(-TAU * fAt(i / x.length) / SR); y1 += a * (x[i] - y1); y2 += a * (y1 - y2); o[i] = y2; } return o; }
const sine = (n, f0, f1 = f0, k = 30) => { const x = new Float32Array(n); let ph = 0; for (let i = 0; i < n; i++) { const t = i / n, f = f1 + (f0 - f1) * Math.exp(-k * t); ph += TAU * f / SR; x[i] = Math.sin(ph); } return x; };
const add = (a, b, at = 0, g = 1) => { for (let i = 0; i < b.length; i++) if (at + i >= 0 && at + i < a.length) a[at + i] += b[i] * g; return a; };
const sum = (n, parts) => { const o = new Float32Array(n); for (const [x, g, at = 0] of parts) add(o, x, Math.round(at * SR), g); return o; };

// ---- voices
function tap(o = {}) {        // a soft wooden "tok" (a key being set), no click on top
  const f = o.pitch ?? 1, n = N(0.18);
  return lp(sum(n, [[env(sine(n, 560 * f, 420 * f, 40), 0.002, 0.03), 1], [env(sine(n, 1130 * f, 900 * f, 40), 0.002, 0.012), 0.18], [env(lp(white(n), 1400), 0.001, 0.008), 0.5]]), 3200);
}
function cup(o = {}) {        // a paper cup set down on a desk
  const f = o.pitch ?? 1, n = N(0.3);
  return lp(sum(n, [[env(sine(n, 150 * f, 95 * f, 25), 0.003, 0.06), 1], [env(lp(white(n), 1100), 0.002, 0.025), 0.8], [env(sine(n, 420 * f, 380 * f, 10), 0.002, 0.03), 0.45]]), 2400);
}
function swell(o = {}) {      // the collapse: a slow, dark breath in, cut short when everything lands on the line
  const len = o.len ?? 1.0, n = N(len + 0.3), x = white(n);
  const y = lpSweep(x, (p) => 180 + 900 * Math.min(1, p * (len + 0.3) / len) ** 2);
  for (let i = 0; i < n; i++) { const t = i / SR, p = Math.min(1, t / len); y[i] *= p ** 2.2 * (t > len ? Math.exp(-(t - len) / 0.06) : 1); }
  const sub = env(sine(n, 70, 110, 1.5), 0.6, 0.35);
  return sum(n, [[y, 2.6], [sub, 0.25, 0]]);
}
function air(o = {}) {        // a gentle, low whoosh
  const len = o.len ?? 0.6, n = N(len), y = lpSweep(white(n), (p) => 250 + 1100 * Math.sin(Math.PI * p));
  for (let i = 0; i < n; i++) { const p = i / n; y[i] *= Math.sin(Math.PI * p) ** 2; }
  return y.map((v) => v * 2.2);
}
function pop(o = {}) {        // the character starts to move: a round, satisfying pop
  const n = N(0.3);
  return lp(sum(n, [[env(sine(n, 520, 230, 18), 0.003, 0.07), 1], [env(sine(n, 1040, 460, 18), 0.003, 0.03), 0.12]]), 2600);
}
function thud(o = {}) {       // the body lands: soft and deep
  const n = N(1.0);
  const first = sum(n, [[env(sine(n, 95, 44, 10), 0.004, 0.2), 1.3], [env(lp(white(n), 420), 0.003, 0.06), 1.0], [env(sine(n, 230, 140, 12), 0.004, 0.07), 0.55], [env(lp(white(n), 900), 0.02, 0.09), 0.4]]);
  const second = sum(n, [[env(sine(n, 80, 48, 14), 0.004, 0.09), 0.5], [env(lp(white(n), 380), 0.003, 0.035), 0.35]]);
  return lp(add(first, second, Math.round(0.24 * SR), 1), 1800);
}
function chime(o = {}) {      // the end: two soft notes a fifth apart, slow attack, long tail, no bright partials
  const n = N(2.4), out = new Float32Array(n);
  [[523.25, 0], [783.99, 0.16]].forEach(([f, d], k) => {
    const m = sum(n, [[env(sine(n, f, f, 0), 0.02, 0.7), 1], [env(sine(n, f * 2, f * 2, 0), 0.02, 0.25), 0.12], [env(sine(n, f * 3, f * 3, 0), 0.02, 0.12), 0.04]]);
    add(out, m, Math.round(d * SR), k ? 0.7 : 1);
  });
  return lp(out, 3500);
}
const V = { tap, cup, swell, air, pop, thud, chime };
const GAIN = { tap: 0.38, cup: 0.45, swell: 0.4, air: 0.35, pop: 0.45, thud: 0.55, chime: 0.3 };
const PAN = { tap: 0, cup: 0.25, swell: 0, air: 0, pop: 0, thud: 0, chime: 0 };

const len = Math.round(DUR * SR), L = new Float32Array(len), Rr = new Float32Array(len), send = new Float32Array(len);
edit.hits.forEach(([t, label, name, o = {}], k) => {
  const v = V[name]; if (!v) throw new Error(`unknown voice ${name}`);
  R = rng(1000 + k * 977);
  const x = v(o), g = (o.gain ?? 1) * GAIN[name], pan = o.pan ?? PAN[name];
  const lead = name === 'swell' ? 0 : name === 'air' ? (o.len ?? 0.6) * 0.5 : 0;   // a whoosh peaks on its mark
  const at = Math.round((t - lead) * SR), gl = g * Math.cos((pan + 1) * Math.PI / 4) * Math.SQRT2, gr = g * Math.sin((pan + 1) * Math.PI / 4) * Math.SQRT2;
  const verb = name === 'chime' ? 0.35 : name === 'thud' ? 0.12 : 0.18;
  for (let i = 0; i < x.length; i++) { const d = at + i; if (d < 0 || d >= len) continue; L[d] += gl * x[i]; Rr[d] += gr * x[i]; send[d] += verb * g * x[i]; }
});
// a small, dark room (a few damped taps), so the sounds sit in a space instead of on the glass
const tapd = (src, dst, ms, g, f) => { const dly = Math.round(ms * SR / 1000), s = lp(src, f); for (let i = dly; i < len; i++) dst[i] += s[i - dly] * g; };
[[29, 0.45, 2600], [67, 0.3, 2000], [131, 0.2, 1500], [211, 0.12, 1200]].forEach(([ms, g, f], i) => { tapd(send, L, ms + i * 3, g, f); tapd(send, Rr, ms + 7 + i * 5, g, f); });
const data = Buffer.alloc(len * 8); let peak = 0;
for (let i = 0; i < len; i++) { data.writeFloatLE(L[i], i * 8); data.writeFloatLE(Rr[i], i * 8 + 4); peak = Math.max(peak, Math.abs(L[i]), Math.abs(Rr[i])); }
const h = Buffer.alloc(44); h.write('RIFF', 0); h.writeUInt32LE(36 + data.length, 4); h.write('WAVE', 8); h.write('fmt ', 12); h.writeUInt32LE(16, 16); h.writeUInt16LE(3, 20); h.writeUInt16LE(2, 22); h.writeUInt32LE(SR, 24); h.writeUInt32LE(SR * 8, 28); h.writeUInt16LE(8, 32); h.writeUInt16LE(32, 34); h.write('data', 36); h.writeUInt32LE(data.length, 40);
fs.writeFileSync(path.join(DIR, 'audio/sfx.wav'), Buffer.concat([h, data]));
console.log(`${edit.hits.length} sounds -> blazma-x2/audio/sfx.wav (peak ${(20 * Math.log10(peak + 1e-9)).toFixed(1)} dBFS)`);
