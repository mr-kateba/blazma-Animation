// The edit decision list for blazma-x: which part of the real screen recording goes where, the creator-style captions, and every sound.
// Reads assets/rec.json (marks measured from the recording), writes assets/edit.json (consumed by film.js and audio/foley.mjs) and film.json.
//   node blazma-x/tools/edit.mjs
import fs from 'node:fs';
import path from 'node:path';
const D = path.join(process.cwd(), 'blazma-x');
const rec = JSON.parse(fs.readFileSync(path.join(D, 'assets/rec.json'), 'utf8'));
const [CLICK_GEN, CLICK_PROMPT, CLICK_OK, CLICK_PLAY] = rec.clicks;
const K = rec.keys;                                   // 32 keystroke frames: 17 letters, the slip, the backspace, 13 more
const FALL = 31.15;                                   // recording second the body lands (measured from the frames)
const STEP0 = 25.42, STRIDE = 0.4;                    // footfalls of the run (measured: one wide stance every 0.4 s)

// ---- segments: rec = [from, to] seconds of the recording, speed = playback rate
const plan = [
  { id: 'intro', d: 3.5 },
  { id: 'rec', name: 'hook', rec: [FALL - 1.3, FALL + 0.5], speed: 1, fx: [[0, 1000]] },
  { id: 'rec', name: 'open', rec: [2.75, 8.2], speed: 1.25, fx: [[0, 1000], [0.8, 600], [1.9, 560], [2.6, 960], [4.4, 960]] },
  { id: 'rec', name: 'type', rec: [8.2, 15.4], speed: 1.2, fx: [[0, 960]] },
  { id: 'rec', name: 'plan', rec: [15.4, 18.85], speed: 1.25, fx: [[0, 960]] },
  { id: 'rec', name: 'ok', rec: [18.85, 20.75], speed: 1.3, fx: [[0, 960]] },
  { id: 'rec', name: 'cut', rec: [21.2, 24.0], speed: 2.0, fx: [[0, 900], [0.9, 640]] },
  { id: 'rec', name: 'run', rec: [24.0, 31.9], speed: 1.0, fx: [[0, 640], [1.4, 640], [2.6, 920]] },
  { id: 'cam', d: 2.5 }, { id: 'bal', d: 2.5, cut: 1.2 }, { id: 'hit', d: 2.5, cut: 1.25 }, { id: 'ai', d: 2.8, cut: 1.4 }, { id: 'end', d: 3.6 },
];
let t = 0; const segs = [];
for (const p of plan) {
  const d = p.rec ? (p.rec[1] - p.rec[0]) / p.speed : p.d;
  segs.push({ ...p, t0: +t.toFixed(4), t1: +(t + d).toFixed(4) }); t += d;
}
const duration = +t.toFixed(3);
const seg = (n) => segs.find((s) => s.name === n || s.id === n);
const toFilm = (r) => segs.filter((s) => s.rec && r >= s.rec[0] && r <= s.rec[1]).map((s) => s.t0 + (r - s.rec[0]) / s.speed);   // all film times that show recording second r
const one = (r) => { const a = toFilm(r); if (!a.length) throw new Error(`rec ${r} not on screen`); return a[0]; };
const S = (n) => seg(n);

// ---- captions (film seconds): Gulf-friendly, short, one style
const hook = S('hook'), open = S('open'), type = S('type'), plan_ = S('plan'), ok = S('ok'), cut = S('cut'), run = S('run');
const inRun = (r) => run.t0 + (r - run.rec[0]) / run.speed;
const typo = one(K[17]), back = one(K[18]);
const caps = [
  [hook.t0 + 0.05, hook.t1 - 0.05, 'كتبت جملة بس.. والشخصية تحرّكت!'],
  [open.t0 + 0.05, one(CLICK_GEN) + 0.9, 'افتح «توليد حركة»'],
  [one(CLICK_GEN) + 1.0, typo - 0.25, 'واكتب اللي تبيه بالعربي'],
  [typo - 0.15, back + 0.75, 'غلطت؟ عادي.. تصلحها'],
  [back + 0.9, type.t1 - 0.05, 'تكتب بلهجتك عادي'],
  [plan_.t0 + 0.25, plan_.t1 - 0.05, 'يفهم كلامك ويقسّمه خطوات'],
  [ok.t0 + 0.05, ok.t1 - 0.05, 'وتضغط «توليد»', 'top'],
  [cut.t0 + 0.05, cut.t1 - 0.05, 'اختصرنا لك وقت التوليد'],
  [inRun(24.9), inRun(26.3) - 0.05, 'في واحد وراك..'],
  [inRun(26.3), inRun(30.45) - 0.05, 'اجري..'],
  [inRun(30.45), run.t1 - 0.05, 'وبعدها موت!'],
];
const cam = S('cam'), bal = S('bal'), hit = S('hit'), ai = S('ai'), end = S('end');
caps.push(
  [cam.t0 + 0.1, cam.t1 - 0.08, 'كاميرا حرة.. من أي زاوية تبي'],
  [bal.t0 + 0.1, bal.t0 + bal.cut - 0.05, 'لو ما تتوازن.. تنهار'], [bal.t0 + bal.cut + 0.05, bal.t1 - 0.08, 'ومع التوازن.. تقف على رجولها'],
  [hit.t0 + 0.1, hit.t1 - 0.08, 'ضربات وتصادم.. وتختبر الإصابة'],
  [ai.t0 + 0.1, ai.t0 + ai.cut - 0.05, 'الذكاء الاصطناعي يشتغل على جهازك'], [ai.t0 + ai.cut + 0.05, ai.t1 - 0.08, 'ويدعم كرت الشاشة'],
);

// ---- sounds (film seconds): voice names are defined in audio/foley.mjs
const hits = [];
const H = (tt, label, v, o) => hits.push([+tt.toFixed(4), label, v, o || {}]);
// keyboard: one sound per real keystroke (letters, spaces, the slip, the backspace)
const seq = [...'في واحد وراك، اجر', 'و', '\b', ...'ي، وبعدها موت'];
K.forEach((k, i) => { const ch = seq[i], tt = one(k); H(tt, 'key', ch === '\b' ? 'backspace' : ch === ' ' ? 'space' : 'key', { pitch: 0.92 + ((i * 37) % 17) / 100 }); });
// mouse: every real press in the recording
[CLICK_GEN, CLICK_PROMPT, CLICK_OK, CLICK_PLAY].forEach((c) => H(one(c), 'click', 'mouse'));
// cuts: a soft whoosh where the picture changes
[open, cam, bal, hit, ai, end].forEach((s) => H(s.t0, 'cut', 'whoosh', { len: 0.32, gain: 0.8 }));
H(cut.t0, 'jump', 'whoosh', { len: 0.4, gain: 0.7 });
H(bal.t0 + bal.cut, 'before/after', 'tick', { pitch: 1250 }); H(hit.t0 + hit.cut, 'hit test', 'tick', { pitch: 1250 });
// the plan appears row by row (3 steps), the dialog's OK, the device list and models window
const planAt = one(14.933 + 0.5 + 0.02);
[0, 0.16, 0.32].forEach((d, i) => H(planAt + d, 'step', 'tick', { pitch: [1180, 1480, 1760][i] }));
H(ok.t0 + 1.0, 'ok', 'tick', { pitch: 900, gain: 0.8 });
H(ai.t0 + 0.12, 'models', 'tick', { pitch: 1320 }); H(ai.t0 + ai.cut + 0.3, 'device list', 'tick', { pitch: 1180 }); H(ai.t0 + ai.cut + 0.8, 'GPU', 'tick', { pitch: 1760 });
H(cam.t0 + 0.9, 'menu', 'tick', { pitch: 1320 }); H(cam.t0 + 1.7, 'settings', 'tick', { pitch: 1100 });
// the run: a footfall every 0.4 s (alternating sides) and the body landing on «وبعدها موت»
for (let r = STEP0, i = 0; r < FALL - 0.6; r += STRIDE, i++) for (const tt of toFilm(r + 0.012 * ((i * 5) % 3 - 1))) H(tt, 'step', 'step', { pan: i % 2 ? 0.18 : -0.18, pitch: 0.95 + 0.1 * ((i * 7) % 3) / 2 });
for (const tt of toFilm(FALL - 0.05)) H(tt, 'fall', 'fall');       // the body lands ~3 frames before the measured mark
for (const tt of toFilm(FALL - 0.02)) hits.push([+(tt - 0.38).toFixed(4), 'kneel', 'step', { gain: 0.7, pitch: 0.8 }]);
// end card: a soft "done"
H(end.t0 + 0.9, 'done', 'chime', { pitch: 880 });
hits.sort((a, b) => a[0] - b[0]);

const edit = { rec: { fps: 30, frames: Math.floor(rec.frames / 2) }, segments: segs, captions: caps.map(([a, b, s, pos]) => [+a.toFixed(3), +b.toFixed(3), s, pos || 'b']), hits, duration };
fs.writeFileSync(path.join(D, 'assets/edit.json'), JSON.stringify(edit));
fs.writeFileSync(path.join(D, 'film.json'), JSON.stringify({ title: 'Blazma X', duration, formats: ['16x9', '1x1'], poster: +(run.t0 + (FALL - run.rec[0]) + 0.35).toFixed(2) }, null, 1));
console.log(`duration ${duration}s; segments:`); for (const s of segs) console.log(' ', (s.name || s.id).padEnd(6), s.t0.toFixed(2), '->', s.t1.toFixed(2));
console.log(`${hits.length} sounds, ${caps.length} captions`);
