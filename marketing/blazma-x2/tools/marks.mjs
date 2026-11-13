// Footage marks and the sound list for blazma-x2 -> assets/edit.json (read by film.js and audio/foley.mjs).
// Times of the recordings are in take seconds (60 fps frame index / 60); everything else is film seconds.
//   node blazma-x2/tools/marks.mjs
import fs from 'node:fs';
import path from 'node:path';
const D = path.join(process.cwd(), 'blazma-x2');
const T0 = { pain: 3.5, turn: 13.9, dlg: 17.6, run: 21.5, feat: 32.0, end: 40.2 }, DUR = 44.6;
// d: UI scale 1.6 take (rawx2). Typing ends at frame 896; the plan shows its three steps from about frame 926.
const d = { from: 14.95, frames: 123, at: 14.95, speed: 1.0, caps: [16.1, 17.55],
  dlg: [356, 176, 1210, 1000],                      // the dialog's rect in the window (source px)
  off: { '16x9': [0, 0], '1x1': [-366, 40] },     // where the window sits on the canvas (1.0x)
  hlBox: [368, 301, 965, 139], hlRows: [364, 528, 1194, 170],   // the prompt box and the three plan rows (source px)
  // feature beats cut from the same take: "Kimodo: available / runs on this computer" and the understood steps
  feat: { '16x9': { at: 18.6, src: [356, 922, 1210, 96], dest: [355, 270], pop: [692, 430] },
          '1x1': { at: 18.6, src: [486, 922, 1080, 96], dest: [0, 300], pop: [272, 470] } },
  plan: { '16x9': { at: 18.6, src: [356, 492, 1210, 210], dest: [355, 330] },
          '1x1': { at: 18.6, src: [362, 492, 1080, 210], dest: [0, 330] } } };
const box = { '16x9': [368, 301, 965, 139], '1x1': [2, 341, 965, 139] };   // the real prompt box on the canvas in the first dialog frame
// r: UI scale 1.0 take (rawx3), the real Kimodo result playing. Click on Play 24.22 s, body lands 31.15 s.
const r = { from: 24.3, frames: 313, at: 24.35, fall: 31.15, say: [24.9, 26.3, 30.45],
  frame: { '16x9': { crop: [380, 40, 1200, 700], dest: [360, 40] }, '1x1': { crop: [440, 40, 1080, 700], dest: [0, 40] } },
  full: { '16x9': [0, 0], '1x1': [440, 0] },
  keysRow: { '16x9': [140, 866, 1765, 34], '1x1': [4, 866, 1072, 34] } };
const run = (s) => T0.run + (s - r.at);

// sounds: few, soft, low-passed. Names are voices in audio/foley.mjs.
const hits = [
  [5.0, 'key set', 'tap', { pitch: 1 }], [5.95, 'key set', 'tap', { pitch: 1.06 }], [6.75, 'key set', 'tap', { pitch: 0.94 }],
  ...[7.6, 9.4, 10.9, 12.3].map((t, i) => [t + 0.2, 'cup down', 'cup', { pitch: 1 - i * 0.04, gain: 0.8 }]),
  [T0.turn + 0.15, 'collapse', 'swell', { len: 1.0 }],
  ...[0, 1, 2].map((i) => [T0.turn + 1.78 + i * 0.36, 'word', 'tap', { pitch: 1.12 - i * 0.06, gain: 0.7 }]),
  [T0.turn + 3.05, 'onto the real box', 'air', { len: 0.6, gain: 0.7 }],
  [run(24.42), 'character moves', 'pop', {}],
  [run(31.15) - 0.04, 'body lands', 'thud', {}],
  [run(31.15) + 1.0, 'window opens', 'air', { len: 0.8, gain: 0.7 }],
  ...[0, 2.05, 4.1, 6.15].map((t, i) => [T0.feat + t, 'feature', 'tap', { pitch: 0.9 + i * 0.05, gain: 0.6 }]),
  [T0.end + 0.35, 'end', 'chime', {}],
];
const edit = { T0, duration: DUR, d, r, box, boxText: { '16x9': 22, '1x1': 22 }, boxBase: 35, boxPad: 11, hits };
fs.writeFileSync(path.join(D, 'assets/edit.json'), JSON.stringify(edit, null, 1));
fs.writeFileSync(path.join(D, 'film.json'), JSON.stringify({ title: 'Blazma X2', duration: DUR, formats: ['16x9', '1x1'], poster: +(run(31.15) + 2.6).toFixed(2) }, null, 1));
console.log(`edit.json: ${hits.length} sounds, ${DUR} s`);
