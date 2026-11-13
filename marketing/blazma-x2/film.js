// Blazma Animation: X cut #2 ("from a thousand keyframes to one sentence"). 16:9 1920x1080 and 1:1 1080x1080. NO music, NO voice.
// A pure function of time (window.seek(t) paints frame t). One beat = one second (beats.json period 1).
//   0.0  Studio Nakhla intro (first 3.5 s of the studio's own clip)
//   3.5  THE PAIN: a generic, stylised manual keyframe editor drawn in code (no real product's UI): a wooden mannequin posed joint by joint,
//        keyframes piling up in the first frames of a 90-frame timeline, a tangle of curves, a clock racing through the night, cups piling up
//  13.9  THE TURN: everything freezes; every keyframe and every curve is pulled into one line, the line opens into a text field and the
//        sentence writes itself, then the field lands exactly on the real prompt box of the real app
//  17.6  THE SOLUTION: real screen recordings of Blazma (UI scale 1.6 take for the dialog, 1.25 take for the run), never scaled above 1.0x
//  31.4  FEATURES: four real captures, 2 s each
//  39.6  CALL TO ACTION
import * as M from './lib/motion.js';
const { W, H, E, prog, lerp, clamp, font, fill, rrect, FORMAT, hash01 } = M;
const SQ = FORMAT.square;
const P = (a, b) => FORMAT.pick({ '16x9': a, '1x1': b });
const DISP = 'Display', UI = 'UI';
// Blazma's own palette (the app's dark theme) + a cold, grey "old way" palette for the pain section
const C = { bg: '#1d1b19', panel: '#252320', raised: '#34312c', line: '#3b3833', text: '#ece7df', text2: '#b4ada2', accent: '#c27a3e',
  cold: '#15171b', coldPanel: '#1b1e23', coldLine: '#2a2f37', coldText: '#8d96a5', coldDim: '#5b6372', wood: '#cdb794', woodDark: '#9c8566', woodLine: '#6f5f49' };
const mixc = M.mix;

// ------------------------------------------------------------------ timeline (seconds)
let T0 = { intro: 0, pain: 3.5, turn: 13.9, dlg: 17.6, run: 21.5, feat: 32.0, end: 40.2 }, DUR = 44.6;   // replaced from assets/edit.json
let EDIT = null;   // footage marks, assets/edit.json (written by tools/marks.mjs from the recordings)

// ------------------------------------------------------------------ type
function T(ctx, str, x, y, size, color, o = {}) {
  const { fam = DISP, w = 600, align = 'center', rtl = true, alpha = 1 } = o;
  if (alpha <= 0.003) return;
  ctx.save(); ctx.font = font(size, w, fam); ctx.fillStyle = color; ctx.textAlign = align; ctx.direction = rtl ? 'rtl' : 'ltr'; ctx.globalAlpha *= alpha; ctx.fillText(str, x, y); ctx.restore();
}
const _m = new Map();
function tw(ctx, str, size, fam = DISP, w = 600, rtl = true) {
  const k = `${str}|${size}|${fam}|${w}|${rtl}`; if (_m.has(k)) return _m.get(k);
  ctx.save(); ctx.font = font(size, w, fam); ctx.direction = rtl ? 'rtl' : 'ltr'; const v = ctx.measureText(str).width; ctx.restore(); _m.set(k, v); return v;
}
// caption: one style for the whole film. Large display face, cream, a soft dark halo instead of a box; enters with a short rise.
function caption(ctx, t, a, b, str, y, o = {}) {
  if (t < a - 0.01 || t > b + 0.01) return;
  const { size = P(80, 68), color = '#f4efe7', halo = 0.55, x = W / 2 } = o;
  const al = Math.min(E.outCubic(prog(t, a, a + 0.28)), 1 - E.inCubic(prog(t, b - 0.22, b)));
  const rise = (1 - E.outCubic(prog(t, a, a + 0.4))) * 14;
  if (halo > 0) {
    const w = tw(ctx, str, size) + size * 2.2, h = size * 2.0, cy = y - size * 0.32;
    ctx.save(); ctx.globalAlpha = al * halo; ctx.translate(x, cy); ctx.scale(1, h / w);
    const g = ctx.createRadialGradient(0, 0, 0, 0, 0, w / 2);
    g.addColorStop(0, 'rgba(10,9,8,0.9)'); g.addColorStop(0.6, 'rgba(10,9,8,0.55)'); g.addColorStop(1, 'rgba(10,9,8,0)');
    ctx.fillStyle = g; ctx.fillRect(-w / 2, -w / 2, w, w); ctx.restore();
  }
  T(ctx, str, x, y + rise, size, color, { alpha: al });
}

// ------------------------------------------------------------------ footage
const FR = { n: {}, d: {}, r: {} };
async function loadSeqs() {
  const jobs = [];
  const want = (k, i) => { if (FR[k][i]) return; const im = new Image(); im.src = `assets/seq/${k}/${String(i).padStart(5, '0')}.jpg`; jobs.push(im.decode().catch(() => {})); FR[k][i] = im; };
  for (let i = 1; i <= 210; i++) want('n', i);
  for (let i = 1; i <= EDIT.d.frames; i++) want('d', i);
  for (let i = 1; i <= EDIT.r.frames; i++) want('r', i);
  await Promise.all(jobs);
}
// frame of take k at take-second s (30 fps JPEG sequences that start at EDIT[k].from)
function frameOf(k, s) {
  const e = EDIT[k], f = clamp((s - e.from) * 30, 0, e.frames - 1), a = Math.floor(f);
  return [FR[k][a + 1], FR[k][Math.min(a + 2, e.frames)], f - a];
}
function drawTake(ctx, k, s, ox, oy, alpha = 1) {
  const [a, b, w] = frameOf(k, s); if (!a) return;
  ctx.save(); ctx.globalAlpha *= alpha; ctx.drawImage(a, ox, oy);
  if (b && w > 0.02) { ctx.globalAlpha *= w; ctx.drawImage(b, ox, oy); }
  ctx.restore();
}

// ------------------------------------------------------------------ pain: the stylised manual editor
const JOINTS = ['hips', 'spine', 'neck', 'head', 'shoulder.L', 'elbow.L', 'hand.L', 'shoulder.R', 'elbow.R', 'hand.R', 'thigh.L', 'knee.L', 'thigh.R', 'knee.R'];
// the edits the animator makes: [start, end, joint, delta degrees]; three slow ones, then faster and faster
const EDITS = (() => {
  const out = [[4.05, 5.0, 'elbow.R', -62], [5.25, 5.95, 'shoulder.L', 48], [6.15, 6.75, 'knee.L', 38]];
  const pool = ['elbow.L', 'shoulder.R', 'knee.R', 'thigh.L', 'spine', 'neck', 'hand.R', 'thigh.R', 'elbow.R', 'shoulder.L', 'head', 'hand.L', 'knee.L', 'hips'];
  let t = 6.95, i = 0;
  while (t < 13.55) {
    const d = Math.max(0.13, 0.5 * Math.exp(-(t - 6.95) / 2.6));
    const j = pool[Math.floor(hash01(i, 7) * pool.length)], s = hash01(i, 9) > 0.5 ? 1 : -1;
    out.push([t, t + d * 0.82, j, s * (6 + 16 * hash01(i, 11))]);
    t += d; i++;
  }
  return out;
})();
// frame the playhead sits on: 1 for the first slow edits, then creeping (1 .. 12 of 90)
const frameAt = (t) => t < 6.9 ? 1 : Math.min(12, 1 + Math.floor((t - 6.9) / 0.6));
// keyframes: one per finished edit (on the joint's row, at the current frame), plus a channel copy the editor adds
const KEYS = (() => {
  const out = [];
  EDITS.forEach(([a, b, j], i) => {
    const f = frameAt(b), row = JOINTS.indexOf(j);
    out.push({ t: b, row, f, i });
    if (i > 2) { const r2 = Math.floor(hash01(i, 21) * JOINTS.length); out.push({ t: b + 0.04, row: r2, f, i: i + 500 }); }
  });
  return out;
})();
const BASE = { hips: 0, spine: 0, neck: 0, head: 0, 'shoulder.L': 14, 'elbow.L': 8, 'hand.L': 0, 'shoulder.R': -14, 'elbow.R': -8, 'hand.R': 0, 'thigh.L': 4, 'knee.L': -2, 'thigh.R': -4, 'knee.R': 2 };
function poseAt(t) {
  const a = { ...BASE };
  for (const [s, e, j, d] of EDITS) { if (t <= s) continue; const p = prog(t, s, e); a[j] += d * (p < 1 ? E.inOutCubic(p) + 0.06 * Math.sin(p * 9) * (1 - p) : 1); }
  // keep it a believable (if awkward) pose: clamp the drift
  for (const k in a) a[k] = clamp(a[k], -55, 55);
  return a;
}
const curEdit = (t) => { for (const ed of EDITS) if (t >= ed[0] - 0.12 && t <= ed[1] + 0.1) return ed; return null; };

// mannequin: 2-D articulated wooden figure. Returns joint screen positions (for the gizmo and the cursor).
function mannequin(ctx, cx, fy, s, A, alpha = 1) {
  const rad = (d) => d * Math.PI / 180, pts = {};
  const dir = (ang) => [Math.sin(rad(ang)), Math.cos(rad(ang))];   // 0 = straight down
  const hip = [cx, fy - 245 * s], sp = rad(A.spine + A.hips * 0.4);
  const chest = [hip[0] + Math.sin(sp) * 150 * s, hip[1] - Math.cos(sp) * 150 * s];
  const nk = rad(A.spine + A.neck), neck = [chest[0] + Math.sin(nk) * 22 * s, chest[1] - Math.cos(nk) * 22 * s];
  const hd = rad(A.spine + A.neck + A.head), head = [neck[0] + Math.sin(hd) * 40 * s, neck[1] - Math.cos(hd) * 40 * s];
  const across = [Math.cos(sp), Math.sin(sp)];
  const limb = (root, angs, lens) => { let p = root, acc = 0; const out = [root]; angs.forEach((g, i) => { acc += g; const d = dir(acc); p = [p[0] + d[0] * lens[i] * s, p[1] + d[1] * lens[i] * s]; out.push(p); }); return out; };
  const shL = [chest[0] - across[0] * 52 * s, chest[1] - across[1] * 52 * s + 10 * s], shR = [chest[0] + across[0] * 52 * s, chest[1] + across[1] * 52 * s + 10 * s];
  const armL = limb(shL, [A['shoulder.L'] + A.spine, A['elbow.L'], A['hand.L']], [98, 92, 26]);
  const armR = limb(shR, [A['shoulder.R'] + A.spine, A['elbow.R'], A['hand.R']], [98, 92, 26]);
  const hpL = [hip[0] - 30 * s, hip[1] + 8 * s], hpR = [hip[0] + 30 * s, hip[1] + 8 * s];
  const legL = limb(hpL, [A['thigh.L'], A['knee.L']], [122, 118]), legR = limb(hpR, [A['thigh.R'], A['knee.R']], [122, 118]);
  Object.assign(pts, { hips: hip, spine: chest, neck, head, 'shoulder.L': shL, 'elbow.L': armL[1], 'hand.L': armL[2], 'shoulder.R': shR, 'elbow.R': armR[1], 'hand.R': armR[2], 'thigh.L': hpL, 'knee.L': legL[1], 'thigh.R': hpR, 'knee.R': legR[1] });
  ctx.save(); ctx.globalAlpha *= alpha; ctx.lineCap = 'round'; ctx.lineJoin = 'round';
  const seg = (a, b, w, c1 = C.wood) => { ctx.strokeStyle = C.woodLine; ctx.lineWidth = (w + 4) * s; ctx.beginPath(); ctx.moveTo(...a); ctx.lineTo(...b); ctx.stroke();
    ctx.strokeStyle = c1; ctx.lineWidth = w * s; ctx.beginPath(); ctx.moveTo(...a); ctx.lineTo(...b); ctx.stroke(); };
  const ball = (p, r) => { ctx.beginPath(); ctx.arc(p[0], p[1], r * s, 0, Math.PI * 2); ctx.fillStyle = C.woodDark; ctx.fill(); ctx.lineWidth = 2 * s; ctx.strokeStyle = C.woodLine; ctx.stroke(); };
  const back = '#b39d7b';
  seg(legL[0], legL[1], 34, back); seg(legL[1], legL[2], 30, back); ball(legL[1], 12); seg(legL[2], [legL[2][0] - 26 * s, legL[2][1] + 4 * s], 20, back);
  seg(armL[0], armL[1], 26, back); seg(armL[1], armL[2], 23, back); ball(armL[1], 10); seg(armL[2], armL[3], 20, back);
  seg(hip, chest, 70); seg([hip[0] - 34 * s, hip[1] + 6 * s], [hip[0] + 34 * s, hip[1] + 6 * s], 40);
  seg([chest[0] - across[0] * 40 * s, chest[1] - across[1] * 40 * s + 6 * s], [chest[0] + across[0] * 40 * s, chest[1] + across[1] * 40 * s + 6 * s], 46);
  seg(chest, neck, 22); ctx.beginPath(); ctx.ellipse(head[0], head[1], 30 * s, 37 * s, hd, 0, Math.PI * 2); ctx.fillStyle = C.wood; ctx.fill(); ctx.lineWidth = 4 * s; ctx.strokeStyle = C.woodLine; ctx.stroke();
  seg(legR[0], legR[1], 34); seg(legR[1], legR[2], 30); ball(legR[1], 12); seg(legR[2], [legR[2][0] + 26 * s, legR[2][1] + 4 * s], 20);
  seg(armR[0], armR[1], 26); seg(armR[1], armR[2], 23); ball(armR[1], 10); seg(armR[2], armR[3], 20);
  ball(shL, 11); ball(shR, 11); ball(hip, 13);
  ctx.restore();
  return pts;
}

// the layout of the pain section per format
const LP = () => SQ
  ? { mx: 330, my: 612, ms: 0.8, tlY: 650, tlH: 430, labW: 150, clock: [96, 300, 48], counter: [1010, 290], graph: [600, 330, 440, 250], capY: 112, cup: 110 }
  : { mx: 820, my: 630, ms: 0.88, tlY: 668, tlH: 412, labW: 230, clock: [170, 300, 70], counter: [1820, 290], graph: [1180, 320, 650, 290], capY: 118, cup: 150 };

function keyPos(L, k) {
  const rows = JOINTS.length, x0 = L.labW + 30, x1 = W - 30, rowH = (L.tlH - 74) / rows;
  return [x0 + (k.f - 0.5) * (x1 - x0) / 90, L.tlY + 62 + (k.row + 0.5) * rowH];
}
function diamond(ctx, x, y, r, color, stroke) {
  ctx.beginPath(); ctx.moveTo(x, y - r); ctx.lineTo(x + r, y); ctx.lineTo(x, y + r); ctx.lineTo(x - r, y); ctx.closePath();
  ctx.fillStyle = color; ctx.fill(); if (stroke) { ctx.lineWidth = 1.5; ctx.strokeStyle = stroke; ctx.stroke(); }
}
// tangled curves of the graph view: seeded sums of sines
function curveY(i, x) { return Math.sin(x * (2.1 + 1.7 * hash01(i, 3)) + 6 * hash01(i, 4)) * (0.55 + 0.3 * hash01(i, 5)) + Math.sin(x * (5.3 + 3 * hash01(i, 6)) + hash01(i, 8) * 9) * 0.32; }
const CURVE_COL = ['#c96a63', '#79a86a', '#6a8dc9'];

function painScene(ctx, t, freeze = null, fadeOut = 0) {
  // freeze: during the turn the pain picture is held at its last frame; fadeOut 0..1 dims the chrome
  const L = LP(), tt = freeze ?? t;
  fill(ctx, C.cold);
  const chrome = 1 - fadeOut;
  // a slow push-in over the whole section: the work closes in on the animator
  ctx.save(); const zs = lerp(1.0, 1.035, E.inOutSine(prog(tt, 3.5, 13.9))); ctx.translate(W / 2, H * 0.4); ctx.scale(zs, zs); ctx.translate(-W / 2, -H * 0.4);
  // floor grid (generic perspective lines)
  ctx.save(); ctx.globalAlpha = 0.5 * chrome; ctx.strokeStyle = C.coldLine; ctx.lineWidth = 1.5;
  for (let i = -9; i <= 9; i++) { ctx.beginPath(); ctx.moveTo(L.mx + i * 60, L.my - 40); ctx.lineTo(L.mx + i * 210, L.tlY); ctx.stroke(); }
  for (let j = 0; j < 4; j++) { const y = L.my - 40 + j * j * 7 + j * 6; ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(W, y); ctx.stroke(); }
  ctx.restore();
  // the mannequin + the gizmo of the joint being edited
  const A = poseAt(tt), pts = mannequin(ctx, L.mx, L.my, L.ms, A, chrome);
  const ed = curEdit(tt);
  if (ed && chrome > 0.01) {
    const [s, e, j, d] = ed, p = pts[j], r = 54 * L.ms, a = Math.min(prog(tt, s - 0.12, s), 1 - prog(tt, e, e + 0.1));
    const sweep = d * Math.PI / 180 * E.inOutCubic(prog(tt, s, e));
    ctx.save(); ctx.globalAlpha = a * chrome; ctx.lineWidth = 3; ctx.strokeStyle = 'rgba(205,215,230,.55)'; ctx.beginPath(); ctx.arc(p[0], p[1], r, 0, Math.PI * 2); ctx.stroke();
    ctx.strokeStyle = '#dfe6f0'; ctx.lineWidth = 6; ctx.beginPath(); ctx.arc(p[0], p[1], r, -Math.PI / 2, -Math.PI / 2 + sweep, sweep < 0); ctx.stroke();
    ctx.setLineDash([4, 6]); ctx.lineWidth = 2; ctx.strokeStyle = 'rgba(205,215,230,.4)'; ctx.beginPath(); ctx.arc(p[0], p[1], r * 0.62, 0, Math.PI * 2); ctx.stroke(); ctx.setLineDash([]);
    ctx.restore();
    const ang = -Math.PI / 2 + sweep; cursor(ctx, p[0] + Math.cos(ang) * r, p[1] + Math.sin(ang) * r, a * chrome);
  }
  clock(ctx, L, tt, chrome);
  const f = frameAt(tt);
  ctx.save(); ctx.globalAlpha = chrome;
  T(ctx, `${f} / 90`, L.counter[0], L.counter[1], P(64, 50), '#e3e8ef', { fam: UI, w: 600, rtl: false, align: 'right' });
  T(ctx, 'الإطار', L.counter[0], L.counter[1] - P(74, 58), P(30, 26), C.coldText, { fam: UI, w: 400, align: 'right' });
  ctx.restore();
  // graph view (curves) slides in at 8.2 and keeps getting busier
  const gIn = E.outCubic(prog(tt, 8.2, 8.8)) * chrome;
  if (gIn > 0.01) {
    const [gx, gy, gw, gh] = L.graph, ox = (1 - gIn) * 60;
    ctx.save(); ctx.globalAlpha = gIn; rrect(ctx, gx + ox, gy, gw, gh, 14); ctx.fillStyle = 'rgba(27,30,35,.95)'; ctx.fill(); ctx.strokeStyle = C.coldLine; ctx.lineWidth = 2; ctx.stroke();
    ctx.clip(); ctx.strokeStyle = 'rgba(80,88,100,.35)'; ctx.lineWidth = 1;
    for (let i = 1; i < 6; i++) { ctx.beginPath(); ctx.moveTo(gx + ox, gy + i * gh / 6); ctx.lineTo(gx + ox + gw, gy + i * gh / 6); ctx.stroke(); }
    const n = Math.min(12, Math.floor(1 + (tt - 8.4) * 2.2));
    for (let i = 0; i < n; i++) curve(ctx, i, gx + ox, gy, gw, gh, prog(tt, 8.4 + i / 2.2, 8.4 + i / 2.2 + 0.45));
    ctx.restore();
  }
  timeline(ctx, L, tt, chrome);
  ctx.restore();
  // cups on the desk in front of the screen (foreground, bottom right): one more every couple of seconds
  [7.6, 9.4, 10.9, 12.3].forEach((c, i) => cup(ctx, W - P(120, 90) - i * L.cup * 1.02, H + 6, L.cup, prog(tt, c, c + 0.35), tt - c, chrome));
  // fatigue: the edges darken as the night goes on
  const vg = 0.55 * prog(tt, 9, 13.6) * chrome;
  if (vg > 0) { ctx.save(); const g = ctx.createRadialGradient(W / 2, H * 0.45, H * 0.35, W / 2, H * 0.45, H * 1.05); g.addColorStop(0, 'rgba(0,0,0,0)'); g.addColorStop(1, `rgba(0,0,0,${vg})`); ctx.fillStyle = g; ctx.fillRect(0, 0, W, H); ctx.restore(); }
  return { L, pts };
}
function curve(ctx, i, gx, gy, gw, gh, p) {
  if (p <= 0) return;
  const col = CURVE_COL[i % 3], n = 80, end = Math.max(2, Math.floor(n * E.outCubic(p)));
  ctx.save(); ctx.strokeStyle = col; ctx.globalAlpha *= 0.9; ctx.lineWidth = 3; ctx.beginPath();
  for (let k = 0; k <= end; k++) { const x = k / n; const y = gy + gh / 2 + curveY(i, x * 4) * gh * 0.36; k ? ctx.lineTo(gx + x * gw, y) : ctx.moveTo(gx + x * gw, y); }
  ctx.stroke();
  for (let k = 8; k <= end; k += 16) { const x = k / n, y = gy + gh / 2 + curveY(i, x * 4) * gh * 0.36; ctx.fillStyle = '#e8edf3'; ctx.fillRect(gx + x * gw - 4, y - 4, 8, 8);
    ctx.strokeStyle = 'rgba(232,237,243,.45)'; ctx.lineWidth = 1.5; ctx.beginPath(); ctx.moveTo(gx + x * gw - 26, y - 9); ctx.lineTo(gx + x * gw + 26, y + 9); ctx.stroke(); }
  ctx.restore();
}
function timeline(ctx, L, tt, chrome) {
  if (chrome <= 0.01) return;
  const x0 = L.labW + 30, x1 = W - 30, rowH = (L.tlH - 74) / JOINTS.length;
  ctx.save(); ctx.globalAlpha = chrome;
  ctx.fillStyle = C.coldPanel; ctx.fillRect(-40, L.tlY, W + 80, L.tlH + 60); ctx.fillStyle = C.coldLine; ctx.fillRect(-40, L.tlY, W + 80, 2);
  for (let f = 0; f <= 90; f += 5) { const x = x0 + f * (x1 - x0) / 90; ctx.fillStyle = f % 10 ? '#2c323b' : '#3b424e'; ctx.fillRect(x, L.tlY + 40, 2, f % 10 ? 10 : 16);
    if (f % 10 === 0) T(ctx, String(f), x, L.tlY + 32, P(20, 16), C.coldDim, { fam: UI, w: 400, rtl: false }); }
  JOINTS.forEach((j, r) => { const y = L.tlY + 62 + r * rowH; if (r % 2) { ctx.fillStyle = 'rgba(255,255,255,.018)'; ctx.fillRect(0, y, W, rowH); }
    T(ctx, j, 24, y + rowH * 0.7, P(Math.min(18, rowH * 0.62), 13), C.coldDim, { fam: UI, w: 400, rtl: false, align: 'left' }); });
  ctx.fillStyle = C.coldLine; ctx.fillRect(L.labW + 10, L.tlY + 2, 2, L.tlH);
  for (const k of KEYS) { if (tt < k.t) continue; const [x, y] = keyPos(L, k), a = E.outBack(prog(tt, k.t, k.t + 0.16)); diamond(ctx, x, y, Math.min(rowH * 0.36, 9) * a, '#c8d0dc', '#15171b'); }
  const f = frameAt(tt), px = x0 + (f - 0.5) * (x1 - x0) / 90; ctx.fillStyle = '#dfe6f0'; ctx.fillRect(px - 1.5, L.tlY + 36, 3, L.tlH - 36);
  ctx.restore();
}
function clock(ctx, L, tt, chrome) {
  const p = prog(tt, 3.6, 13.6), mins = 21 * 60 + 370 * (0.3 * p + 0.7 * E.inQuad(p));
  const h = Math.floor(mins / 60) % 24, m = Math.floor(mins % 60), h12 = ((h + 11) % 12) + 1;
  const [cx, cy, r] = L.clock;
  ctx.save(); ctx.globalAlpha = chrome; ctx.lineCap = 'round';
  ctx.beginPath(); ctx.arc(cx, cy, r, 0, Math.PI * 2); ctx.fillStyle = C.coldPanel; ctx.fill(); ctx.lineWidth = 3; ctx.strokeStyle = '#3b424e'; ctx.stroke();
  for (let i = 0; i < 12; i++) { const a = i / 12 * Math.PI * 2; ctx.strokeStyle = '#5b6372'; ctx.lineWidth = i % 3 ? 2 : 4; ctx.beginPath(); ctx.moveTo(cx + Math.sin(a) * r * 0.78, cy - Math.cos(a) * r * 0.78); ctx.lineTo(cx + Math.sin(a) * r * 0.9, cy - Math.cos(a) * r * 0.9); ctx.stroke(); }
  const ma = (mins % 60) / 60 * Math.PI * 2, ha = ((mins / 60) % 12) / 12 * Math.PI * 2;
  ctx.strokeStyle = '#dfe6f0'; ctx.lineWidth = 5; ctx.beginPath(); ctx.moveTo(cx, cy); ctx.lineTo(cx + Math.sin(ha) * r * 0.5, cy - Math.cos(ha) * r * 0.5); ctx.stroke();
  ctx.lineWidth = 3; ctx.beginPath(); ctx.moveTo(cx, cy); ctx.lineTo(cx + Math.sin(ma) * r * 0.74, cy - Math.cos(ma) * r * 0.74); ctx.stroke();
  ctx.beginPath(); ctx.arc(cx, cy, 5, 0, Math.PI * 2); ctx.fillStyle = '#dfe6f0'; ctx.fill();
  T(ctx, `${h12}:${String(m).padStart(2, '0')} ${h >= 12 ? 'م' : 'ص'}`, cx, cy + r + P(46, 38), P(32, 26), C.coldText, { fam: UI, w: 600 });
  ctx.restore();
}
function cursor(ctx, x, y, a = 1, s = 1.25) {
  if (a <= 0.01) return;
  const pts = [[0, 0], [0, 24], [5.5, 19], [9.5, 28.5], [13, 27], [9, 18], [17, 18]];
  ctx.save(); ctx.globalAlpha *= a; ctx.translate(x, y); ctx.scale(s, s);
  ctx.beginPath(); pts.forEach(([px, py], i) => (i ? ctx.lineTo(px + 1.5, py + 2.5) : ctx.moveTo(px + 1.5, py + 2.5))); ctx.closePath(); ctx.fillStyle = 'rgba(0,0,0,.35)'; ctx.fill();
  ctx.beginPath(); pts.forEach(([px, py], i) => (i ? ctx.lineTo(px, py) : ctx.moveTo(px, py))); ctx.closePath(); ctx.fillStyle = '#fff'; ctx.fill(); ctx.lineWidth = 1.6; ctx.strokeStyle = '#111'; ctx.stroke();
  ctx.restore();
}
function cup(ctx, x, base, w, inP, age, chrome) {
  if (inP <= 0 || chrome <= 0.01) return;
  const drop = (1 - E.outBack(inP, 1.2)) * 60, h = w * 1.12, y = base - h + drop;
  ctx.save(); ctx.globalAlpha = Math.min(1, inP * 3) * chrome;
  ctx.beginPath(); ctx.moveTo(x - w * 0.42, y); ctx.lineTo(x + w * 0.42, y); ctx.lineTo(x + w * 0.34, base + drop); ctx.lineTo(x - w * 0.34, base + drop); ctx.closePath();
  const g = ctx.createLinearGradient(x - w / 2, 0, x + w / 2, 0); g.addColorStop(0, '#0d0e10'); g.addColorStop(0.55, '#262a30'); g.addColorStop(1, '#0d0e10'); ctx.fillStyle = g; ctx.fill();
  rrect(ctx, x - w * 0.46, y - w * 0.07, w * 0.92, w * 0.1, w * 0.04); ctx.fillStyle = '#2b2f35'; ctx.fill();
  ctx.fillStyle = 'rgba(200,210,225,.22)'; ctx.fillRect(x - w * 0.44, y - w * 0.07, w * 0.88, 2);
  rrect(ctx, x - w * 0.36, y + h * 0.36, w * 0.72, h * 0.2, 4); ctx.fillStyle = '#1a1c20'; ctx.fill();
  ctx.strokeStyle = 'rgba(210,218,230,.16)'; ctx.lineWidth = w * 0.05; ctx.lineCap = 'round';
  for (let k = 0; k < 2; k++) { ctx.beginPath(); for (let i = 0; i <= 20; i++) { const v = i / 20, yy = y - w * 0.15 - v * w * 0.8, xx = x + (k - 0.5) * w * 0.22 + Math.sin(v * 5 + age * 2.2 + k * 2) * w * 0.07 * v;
    i ? ctx.lineTo(xx, yy) : ctx.moveTo(xx, yy); } ctx.globalAlpha = Math.min(1, inP * 3) * chrome * clamp(age / 0.8); ctx.stroke(); }
  ctx.restore();
}

// ------------------------------------------------------------------ the turn
const SENT = ['في واحد وراك،', 'اجري،', 'وبعدها موت'];
const FIELD = () => SQ ? { x: 90, y: 470, w: 900, h: 140, size: 54 } : { x: 380, y: 470, w: 1160, h: 150, size: 62 };
function turnScene(ctx, t) {
  const L = LP(), dt = t - T0.turn;
  const fadeOut = E.inOutCubic(prog(dt, 0.25, 0.8));
  painScene(ctx, t, T0.turn - 0.02, fadeOut);
  const warm = E.inOutSine(prog(dt, 0.9, 1.6)); if (warm > 0) { ctx.save(); ctx.globalAlpha = warm; fill(ctx, C.bg); ctx.restore(); }
  // everything collapses into one line: every keyframe and every curve point flies to the line
  const Fd = FIELD(), ly = Fd.y + Fd.h / 2, lx0 = Fd.x + 30, lx1 = Fd.x + Fd.w - 30;
  const col = (p) => mixc('#c8d0dc', C.accent, p);
  const live = KEYS.filter((k) => k.t <= T0.turn), n = live.length;
  const pull = (i) => E.inOutCubic(prog(dt, 0.15 + 0.35 * hash01(i, 31), 0.85 + 0.25 * hash01(i, 33)));
  if (dt < 1.45) {
    // (the pain picture had a slight push-in; the keys start from where they were on screen)
    const zs = lerp(1.0, 1.035, 1), zx = (x) => W / 2 + (x - W / 2) * zs, zy = (y) => H * 0.4 + (y - H * 0.4) * zs;
    live.forEach((k, i) => { const [x0, y0] = keyPos(L, k), x = zx(x0), y = zy(y0), p = pull(i); const tx = lerp(lx0, lx1, (i + 0.5) / n);
      const xx = lerp(x, tx, p), yy = lerp(y, ly, p) - Math.sin(p * Math.PI) * 60 * hash01(i, 35);
      ctx.save(); ctx.globalAlpha = 1 - prog(dt, 1.15, 1.45); diamond(ctx, xx, yy, lerp(8, 4, p), col(p)); ctx.restore(); });
    const [gx, gy, gw, gh] = L.graph, flat = E.inOutCubic(prog(dt, 0.1, 1.0));
    ctx.save(); ctx.globalAlpha = 1 - prog(dt, 1.0, 1.3); ctx.lineWidth = 3;
    for (let i = 0; i < 12; i++) { ctx.strokeStyle = mixc(CURVE_COL[i % 3], C.accent, flat); ctx.beginPath();
      for (let k = 0; k <= 80; k++) { const x = k / 80, y0 = zy(gy + gh / 2 + curveY(i, x * 4) * gh * 0.36), x0 = zx(gx + x * gw);
        const xx = lerp(x0, lerp(lx0, lx1, x), flat), yy = lerp(y0, ly, flat); k ? ctx.lineTo(xx, yy) : ctx.moveTo(xx, yy); }
      ctx.stroke(); }
    ctx.restore();
  }
  // the line, then it opens into a field; then the field travels onto the real prompt box
  const lineIn = E.outCubic(prog(dt, 0.7, 1.1)), open = E.inOutCubic(prog(dt, 1.25, 1.75));
  const toReal = E.inOutCubic(prog(dt, 3.0, 3.6));
  const R = EDIT.box[FORMAT.name];
  let fx = Fd.x, fy = ly - lerp(2, Fd.h / 2, open), fw = Fd.w, fh = lerp(4, Fd.h, open);
  fx = lerp(fx, R[0], toReal); fy = lerp(fy, R[1], toReal); fw = lerp(fw, R[2], toReal); fh = lerp(fh, R[3], toReal);
  if (toReal > 0) dialogFrame(ctx, T0.dlg, E.inOutSine(prog(dt, 3.1, 3.65)));
  if (lineIn > 0) {
    ctx.save(); ctx.globalAlpha = lineIn * (1 - prog(dt, 3.4, 3.65));
    rrect(ctx, fx, fy, fw, fh, lerp(2, 12, open)); ctx.fillStyle = mixc(C.accent, '#1a1816', open); ctx.fill();
    ctx.lineWidth = 3; ctx.strokeStyle = C.accent; ctx.stroke(); ctx.restore();
  }
  caption(ctx, t, T0.turn + 0.85, T0.turn + 2.95, 'طيب.. وإذا كتبتها بس؟', Fd.y - P(90, 80), { halo: 0 });
  // the sentence writes itself, word by word, right to left
  if (dt > 1.6) {
    const sz = lerp(Fd.size, EDIT.boxText[FORMAT.name], toReal), right = fx + fw - lerp(40, EDIT.boxPad, toReal);
    const base = lerp(fy + fh / 2 + sz * 0.36, fy + EDIT.boxBase, toReal);
    let x = right; const sp = tw(ctx, ' ', sz, DISP, 500);
    ctx.save(); ctx.globalAlpha = 1 - prog(dt, 3.35, 3.6);
    SENT.forEach((w, i) => { const a = E.outCubic(prog(dt, 1.75 + i * 0.36, 2.0 + i * 0.36)); const ww = tw(ctx, w, sz, DISP, 500);
      T(ctx, w, x, base + (1 - a) * 10, sz, '#f3eee6', { w: 500, align: 'right', alpha: a }); x -= ww + sp; });
    const ca = (dt > 2.75 && dt < 3.3 && Math.floor((dt - 2.75) * 2.4) % 2 === 0) ? 1 : 0; if (ca) { ctx.fillStyle = C.accent; ctx.fillRect(x + sp - 8, base - sz * 0.85, 3, sz * 1.05); }
    ctx.restore();
  }
}

// ------------------------------------------------------------------ the solution: real dialog (UI scale 1.6 take)
function dialogFrame(ctx, t, alpha = 1) {
  const e = EDIT.d, s = e.at + (t - T0.dlg) * e.speed, off = e.off[FORMAT.name], dl = e.dlg;
  ctx.save(); ctx.globalAlpha = alpha; fill(ctx, C.bg);
  drawTake(ctx, 'd', s, off[0], off[1]);
  ctx.fillStyle = 'rgba(16,14,12,.86)'; ctx.beginPath(); ctx.rect(0, 0, W, H); ctx.roundRect(dl[0] + off[0], dl[1] + off[1], dl[2], dl[3], 10); ctx.fill('evenodd');
  // the window's bottom edge cuts the dialog's buttons: let it sink into the background instead of a hard edge
  const g = ctx.createLinearGradient(0, H - 150, 0, H); g.addColorStop(0, 'rgba(29,27,25,0)'); g.addColorStop(1, 'rgba(29,27,25,1)'); ctx.fillStyle = g; ctx.fillRect(0, H - 150, W, 150);
  ctx.restore();
}
function dlgScene(ctx, t) {
  dialogFrame(ctx, t, 1);
  const e = EDIT.d, film = (s) => T0.dlg + (s - e.at) / e.speed;
  const y = P(94, 118);
  caption(ctx, t, T0.dlg + 0.05, film(e.caps[0]), 'تكتبها بكلامك..', y);
  caption(ctx, t, film(e.caps[0]) + 0.05, film(e.caps[1]), 'ويفهمها خطوات', y);
  caption(ctx, t, film(e.caps[1]) + 0.05, T0.run - 0.02, 'ويولّد الحركة على جهازك', y);
  // guide the eye: the prompt box while «تكتبها بكلامك», then the three understood steps
  const off = e.off[FORMAT.name];
  [[e.hlBox, T0.dlg + 0.1, film(e.caps[0])], [e.hlRows, film(e.caps[0]) + 0.1, film(e.caps[1]) + 0.2]].forEach(([r, a, b]) => {
    const al = Math.min(E.outCubic(prog(t, a, a + 0.3)), 1 - prog(t, b - 0.2, b)); if (al <= 0) return;
    ctx.save(); ctx.globalAlpha = al; ctx.lineWidth = 4; ctx.strokeStyle = C.accent; rrect(ctx, r[0] + off[0] - 6, r[1] + off[1] - 6, r[2] + 12, r[3] + 12, 12); ctx.stroke(); ctx.restore(); });
  const na = Math.min(prog(t, film(e.caps[1]) + 0.25, film(e.caps[1]) + 0.5), 1 - prog(t, T0.run - 0.2, T0.run));
  T(ctx, '(وقت الانتظار مختصر)', W / 2, y + P(52, 54), P(28, 28), C.text2, { fam: UI, w: 400, alpha: na });
}

// ------------------------------------------------------------------ the run (UI scale 1.25 take): viewport crop at 1.0x, then the window opens to show the generated keys
function runScene(ctx, t) {
  const e = EDIT.r, s = e.at + (t - T0.run), fr = e.frame[FORMAT.name];
  const film = (ss) => T0.run + (ss - e.at);
  const open = E.inOutCubic(prog(t, film(e.fall) + 1.0, film(e.fall) + 1.9));
  const full = e.full[FORMAT.name];
  const sx = lerp(fr.crop[0], full[0], open), sy = lerp(fr.crop[1], full[1], open), sw = lerp(fr.crop[2], W, open), sh = lerp(fr.crop[3], H, open);
  const dx = lerp(fr.dest[0], 0, open), dy = lerp(fr.dest[1], 0, open);
  fill(ctx, C.bg);
  ctx.save(); rrect(ctx, dx, dy, sw, sh, lerp(16, 0, open)); ctx.clip();
  drawTake(ctx, 'r', s, dx - sx, dy - sy);
  ctx.restore();
  if (open < 1) { ctx.save(); ctx.globalAlpha = 1 - open; rrect(ctx, dx, dy, sw, sh, 16); ctx.lineWidth = 2; ctx.strokeStyle = C.line; ctx.stroke(); ctx.restore(); }
  const cy = P(905, 905), fade = 1 - open;
  ctx.save(); ctx.globalAlpha = fade;
  caption(ctx, t, film(e.say[0]), film(e.say[1]) - 0.05, 'في واحد وراك..', cy, { halo: 0 });
  caption(ctx, t, film(e.say[1]), film(e.say[2]) - 0.05, 'اجري..', cy, { halo: 0 });
  caption(ctx, t, film(e.say[2]), film(e.fall) + 1.2, 'وبعدها موت!', cy, { halo: 0 });
  ctx.restore();
  if (open > 0) {
    const hl = e.keysRow[FORMAT.name], a = E.outCubic(prog(t, film(e.fall) + 1.9, film(e.fall) + 2.3));
    ctx.save(); ctx.globalAlpha = a; ctx.lineWidth = 4; ctx.strokeStyle = C.accent; rrect(ctx, hl[0], hl[1], hl[2], hl[3], 10); ctx.stroke(); ctx.restore();
    caption(ctx, t, film(e.fall) + 2.0, T0.feat - 0.02, 'ولا كي فريم واحد بيدك', P(330, 300), { halo: 0.9 });
  }
}

// ------------------------------------------------------------------ features (real captures, 2 s each, never above 1.0x)
const FEAT = [
  { t: 0.0, cap: 'الذكاء الاصطناعي يشتغل على جهازك', sub: 'بدون سحابة.. ويدعم كرت الشاشة' },
  { t: 2.05, cap: 'توازن وفيزياء.. حركة طبيعية', sub: '' },
  { t: 4.1, cap: 'كاميرا حرة.. من أي زاوية', sub: '' },
  { t: 6.15, cap: 'يفهم العربي.. حتى باللهجة', sub: 'في واحد وراك  ·  اجري  ·  موت' },
];
let IM = {};
function card(ctx, img, src, x, y, o = {}) {        // a real capture at its own pixel size
  const { alpha = 1, r = 14 } = o; if (!img || alpha <= 0.01) return;
  const [sx, sy, sw, sh] = src || [0, 0, img.width, img.height];
  ctx.save(); ctx.globalAlpha *= alpha; ctx.shadowColor = 'rgba(0,0,0,.55)'; ctx.shadowBlur = 50; ctx.shadowOffsetY = 18; rrect(ctx, x, y, sw, sh, r); ctx.fillStyle = C.panel; ctx.fill(); ctx.restore();
  ctx.save(); ctx.globalAlpha *= alpha; rrect(ctx, x, y, sw, sh, r); ctx.clip(); ctx.drawImage(img, sx, sy, sw, sh, x, y, sw, sh); ctx.restore();
  ctx.save(); ctx.globalAlpha *= alpha; rrect(ctx, x + 1, y + 1, sw - 2, sh - 2, r); ctx.lineWidth = 2; ctx.strokeStyle = '#4a463f'; ctx.stroke(); ctx.restore();
}
function featScene(ctx, t) {
  const dt = t - T0.feat; fill(ctx, C.bg);
  let k = 0; for (let i = 0; i < FEAT.length; i++) if (dt >= FEAT[i].t) k = i;
  const f = FEAT[k], ft = dt - f.t, nxt = FEAT[k + 1] ? FEAT[k + 1].t - f.t : T0.end - T0.feat - f.t;
  const inP = E.outCubic(prog(ft, 0, 0.35)), push = (1 - inP) * -90;
  ctx.save(); ctx.translate(push, 0);   // the new capture is on screen from its first frame; it only settles into place
  const slow = (a0, a1) => lerp(a0, a1, E.inOutSine(prog(ft, 0, nxt)));
  if (k === 0) {        // the real Generate dialog (1.6 take): "Kimodo (local AI) · runs on this computer", plus the real device list
    const fe = EDIT.d.feat[FORMAT.name];
    ctx.save(); rrect(ctx, fe.dest[0], fe.dest[1] + slow(8, 0), fe.src[2], fe.src[3], 14); ctx.clip(); drawTake(ctx, 'd', fe.at, fe.dest[0] - fe.src[0], fe.dest[1] + slow(8, 0) - fe.src[1]); ctx.restore();
    ctx.save(); rrect(ctx, fe.dest[0], fe.dest[1] + slow(8, 0), fe.src[2], fe.src[3], 14); ctx.lineWidth = 2; ctx.strokeStyle = '#4a463f'; ctx.stroke(); ctx.restore();
    card(ctx, IM.devPop, null, fe.pop[0], fe.pop[1] + slow(10, 0), { alpha: E.outCubic(prog(ft, 0.5, 0.8)) });
  } else if (k === 1) { // balance: before (red strip) cut to after (green)
    const after = ft > 0.95, im = after ? IM.after : IM.before, src = SQ ? [380, 40, 960, 760] : [80, 40, 1360, 760];
    card(ctx, im, src, (W - src[2]) / 2, 60 + slow(6, 0));
    T(ctx, after ? 'بعد' : 'قبل', (W - src[2]) / 2 + src[2] - 36, 150 + slow(6, 0), 48, after ? '#8fc28a' : '#d98a7f', { fam: UI, w: 600, align: 'right' });
  } else if (k === 2) { // free camera: the real viewport with the real view menu and camera settings
    const src = SQ ? [300, 60, 960, 720] : [80, 60, 1360, 740], x0 = (W - src[2]) / 2;
    card(ctx, IM.cam, src, x0, 70 + slow(6, 0));
    card(ctx, IM.camMenu, null, x0 + P(60, 40), P(130, 110), { alpha: E.outCubic(prog(ft, 0.35, 0.6)) });
    card(ctx, IM.camSet, null, x0 + P(360, 40), P(130, 462), { alpha: E.outCubic(prog(ft, 0.75, 1.0)) });
  } else {              // Arabic understanding: the real plan of the dialect sentence, every step marked «مفهومة» (understood)
    const fe = EDIT.d.plan[FORMAT.name], y = fe.dest[1] + slow(8, 0);
    ctx.save(); rrect(ctx, fe.dest[0], y, fe.src[2], fe.src[3], 14); ctx.clip(); drawTake(ctx, 'd', fe.at, fe.dest[0] - fe.src[0], y - fe.src[1]); ctx.restore();
    ctx.save(); rrect(ctx, fe.dest[0], y, fe.src[2], fe.src[3], 14); ctx.lineWidth = 2; ctx.strokeStyle = '#4a463f'; ctx.stroke(); ctx.restore();
  }
  ctx.restore();
  const ca = Math.min(E.outCubic(prog(ft, 0.05, 0.3)), k === FEAT.length - 1 ? 1 - prog(ft, nxt - 0.2, nxt) : 1);
  const capY = k === 0 || k === 3 ? P(720, 740) : P(930, 910), subY = capY + P(72, 70);
  ctx.save(); const g = ctx.createLinearGradient(0, capY - 170, 0, H); g.addColorStop(0, 'rgba(29,27,25,0)'); g.addColorStop(0.35, 'rgba(29,27,25,.97)'); g.addColorStop(1, C.bg); ctx.fillStyle = g; ctx.fillRect(0, capY - 170, W, H); ctx.restore();
  T(ctx, f.cap, W / 2, capY + (1 - ca) * 10, P(72, 58), '#f4efe7', { alpha: ca });
  if (f.sub) T(ctx, f.sub, W / 2, subY, P(38, 34), C.accent, { fam: UI, w: 600, alpha: ca });
}

// ------------------------------------------------------------------ call to action
function endScene(ctx, t) {
  const dt = t - T0.end; fill(ctx, C.bg);
  const A = (a, d = 0.45) => E.outCubic(prog(dt, a, a + d));
  const is = P(250, 220), iy = P(110, 100) + (1 - A(0)) * 16;
  ctx.save(); ctx.globalAlpha = A(0); rrect(ctx, W / 2 - is / 2, iy, is, is, is * 0.22); ctx.clip(); ctx.drawImage(IM.logo, W / 2 - is / 2, iy, is, is); ctx.restore();
  T(ctx, 'تعال جرّب Blazma', W / 2, P(500, 470) + (1 - A(0.3)) * 14, P(110, 96), '#f4efe7', { alpha: A(0.3) });
  T(ctx, 'حمّله من GitHub', W / 2, P(610, 580) + (1 - A(0.8)) * 12, P(62, 56), C.accent, { alpha: A(0.8) });
  T(ctx, 'github.com/mr-kateba/blazma-Animation', W / 2, P(680, 650) + (1 - A(1.1)) * 10, P(36, 34), C.text2, { fam: UI, w: 400, rtl: false, alpha: A(1.1) });
  const ca = A(1.7), lw = P(170, 160), lh = lw * IM.nk.height / IM.nk.width, ly = P(800, 790);
  ctx.save(); ctx.globalAlpha = ca; ctx.drawImage(IM.nk, W / 2 - lw / 2, ly + (1 - ca) * 10, lw, lh); ctx.restore();
  T(ctx, 'من استوديو نخلة', W / 2, ly + lh + P(48, 46), P(34, 32), C.text2, { fam: UI, w: 400, alpha: ca });
  const out = prog(t, DUR - 0.35, DUR); if (out > 0) { ctx.fillStyle = `rgba(0,0,0,${out})`; ctx.fillRect(0, 0, W, H); }
}

// ------------------------------------------------------------------ intro
function introScene(ctx, t) {
  const f = Math.min(Math.floor(t * 60) + 1, 210), im = FR.n[f] || FR.n[210]; fill(ctx, '#06140f'); if (!im) return;
  if (SQ) ctx.drawImage(im, 420, 0, 1080, 1080, 0, 0, W, H); else ctx.drawImage(im, 0, 0, W, H);
  const d = prog(t, T0.pain - 0.25, T0.pain); if (d > 0) { ctx.fillStyle = `rgba(21,23,27,${d})`; ctx.fillRect(0, 0, W, H); }
}
function painWithCaptions(ctx, t) {
  painScene(ctx, t);
  const L = LP();
  caption(ctx, t, 3.95, 6.7, 'تحرّك كل مفصل.. بيدك', L.capY);
  caption(ctx, t, 6.85, 9.95, 'كي فريم.. ورا كي فريم', L.capY);
  caption(ctx, t, 10.15, 13.75, 'ساعات.. عشان حركة ثلاث ثواني؟', L.capY);
}

// sounds: assets/edit.json "hits" (seconds); audio/foley.mjs voices them, soft and sparse
const HITS = [];

M.film({
  fonts: [font(100, 600, DISP), font(100, 500, DISP), font(40, 400, UI), font(40, 600, UI)],
  images: {
    logo: 'assets/logo-1024.png', nk: 'assets/nakhla/nakhla-logo-ondark.png', devPop: 'assets/v/reel-ar-device-popup.png',
    before: 'assets/v/p6-ar-dark-unbalanced-before.png', after: 'assets/v/p6-ar-dark-balanced-after.png',
    cam: 'assets/v/p10-ar-dark-camera-viewport.png', camMenu: 'assets/v/p10-ar-dark-camera-view-menu.png', camSet: 'assets/v/p10-ar-dark-camera-settings.png',
  },
  hits: HITS,
  samples: (t) => (t < T0.pain || (t > T0.dlg && t < T0.feat) ? 3 : 6),
  async init() {
    EDIT = await (await fetch('assets/edit.json')).json(); T0 = { intro: 0, ...EDIT.T0 }; DUR = EDIT.duration;
    HITS.push(...EDIT.hits.map(([s, l, v, o]) => [s, l, v, o || {}]));
    await loadSeqs();
  },
  draw(ctx, u, t, IMG) {
    IM = IMG;
    if (t < T0.pain) introScene(ctx, t);
    else if (t < T0.turn) painWithCaptions(ctx, t);
    else if (t < T0.dlg) turnScene(ctx, t);
    else if (t < T0.run) dlgScene(ctx, t);
    else if (t < T0.feat) runScene(ctx, t);
    else if (t < T0.end) featScene(ctx, t);
    else endScene(ctx, t);
  },
});
