// Blazma Animation explainer (Arabic, RTL). Pure function of time; timing in beats (u) on beats.json.
// Plan: docs/shotlist.md. Every screen is a real screenshot or real footage frame; no invented UI.
import * as M from './lib/motion.js';
const { W, H, E, prog, lerp, clamp, springU, SPRING, wobble, kf, font, fill, rrect, shake, noise1 } = M;

const C = {
  bg: '#1d1b19', panel: '#252320', panel2: '#2c2a26', raised: '#34312c', line: '#3b3833', lineStrong: '#4a463f',
  text: '#ece7df', text2: '#b4ada2', text3: '#857e73', accent: '#c27a3e', onAccent: '#1d1b19',
};
const DISP = 'Display', UI = 'UI';
const XR = 1824;                       // right edge of the text column (RTL: reading starts here)

// ------------------------------------------------------------------ type (Arabic: whole words, direction rtl)
function T(ctx, str, x, y, size, color, o = {}) {
  const { fam = DISP, w = 600, align = 'right', rtl = true, alpha = 1 } = o;
  ctx.save(); ctx.font = font(size, w, fam); ctx.fillStyle = color; ctx.textAlign = align;
  ctx.direction = rtl ? 'rtl' : 'ltr'; ctx.globalAlpha = alpha; ctx.fillText(str, x, y); ctx.restore();
}
const _m = new Map();
function tw(ctx, str, size, fam = DISP, w = 600, rtl = true) {
  const k = `${str}|${size}|${fam}|${w}`;
  if (_m.has(k)) return _m.get(k);
  ctx.save(); ctx.font = font(size, w, fam); ctx.direction = rtl ? 'rtl' : 'ltr';
  const v = ctx.measureText(str).width; ctx.restore(); _m.set(k, v); return v;
}
function wrap(ctx, str, size, maxW, fam, w) {
  const out = []; let cur = [];
  for (const wd of str.split(' ')) {
    const t = [...cur, wd].join(' ');
    if (cur.length && tw(ctx, t, size, fam, w) > maxW) { out.push(cur); cur = [wd]; } else cur.push(wd);
  }
  out.push(cur); return out;
}
// Words rise out of a mask line by line (direction + weight, no fade). Returns line count.
function slam(ctx, u, u0, str, xr, y, size, color, o = {}) {
  const { maxW = 640, fam = DISP, w = 600, lh = 1.2, delay = 0.3, cfg = SPRING.bouncy, whole = false } = o;
  const L = whole ? [[str]] : wrap(ctx, str, size, maxW, fam, w), gap = tw(ctx, ' ', size, fam, w) || size * 0.25;
  let idx = 0;
  L.forEach((ws, li) => {
    let x = xr; const base = y + li * size * lh;
    ws.forEach((wd) => {
      const ww = tw(ctx, wd, size, fam, w), k = springU(u, u0 + idx * delay, cfg); idx++;
      if (k > 0.001) {
        ctx.save(); ctx.beginPath(); ctx.rect(x - ww - 60, base - size * 1.1, ww + 120, size * 1.8); ctx.clip();
        T(ctx, wd, x, base + (1 - k) * size * 1.0, size, color, { fam, w }); ctx.restore();
      }
      x -= ww + gap;
    });
  });
  return L.length;
}
function bar(ctx, u, u0, xr, y, w, h = 8) {
  const p = E.outExpo(prog(u, u0, u0 + 0.8)); if (p <= 0) return;
  rrect(ctx, xr - w * p, y, w * p, h, h / 2); ctx.fillStyle = C.accent; ctx.fill();
}
function headline(ctx, u, u0, str, sub, o = {}) {
  const { size = 128, y = 400, maxW = 640, color = C.text, subSize = 52, accentWords = 0 } = o;
  const n = slam(ctx, u, u0, str, XR, y, size, color, { maxW });
  const yb = y + (n - 1) * size * 1.2 + 44;
  bar(ctx, u, u0 + 0.8, XR, yb, 150, 9);
  if (sub) slam(ctx, u, u0 + 1.2, sub, XR, yb + 100, subSize, C.text2, { maxW, fam: UI, w: 400, lh: 1.45, delay: 0.2, cfg: SPRING.snappy });
}

// ------------------------------------------------------------------ real assets
let LOGO = null, BLUR = null;
// Real app footage recorded natively at 1920x1080 / 60 fps (Godot --write-movie, UI scale 1.25), kept as JPEG q2 frames.
let REC = null, NK = null;
const SEQ = { h: 60 }, SQ = { h: {} };
async function loadSeq() {
  REC = await (await fetch('assets/rec.json')).json(); SEQ.h = REC.fps;
  const jobs = [];
  for (const [a, b] of REC.use) {
    for (let i = Math.floor(a * REC.fps); i <= Math.ceil(b * REC.fps) + 2; i++) {
      if (SQ.h[i] || i < 1 || i > REC.frames) continue;
      const im = new Image(); im.src = `assets/seq/h/${String(i).padStart(5, '0')}.jpg`;
      jobs.push(new Promise((r) => { im.onload = r; im.onerror = r; })); SQ.h[i] = im;
    }
  }
  await Promise.all(jobs);
}
// footage frame at time `sec` (recording seconds), blended with its neighbour so slow motion stays smooth
function vf(k, sec) {
  const f = Math.max(0, sec) * SEQ[k], a = Math.floor(f);
  return [[SQ[k][a + 1] || SQ[k][a], 1], [SQ[k][a + 2], f - a]];
}
function card(ctx, imgs, box, src, o = {}) {
  const { r = 18, shadow = true, border = true } = o; const [x, y, w, h] = box;
  ctx.save();
  if (shadow) { ctx.shadowColor = 'rgba(0,0,0,.55)'; ctx.shadowBlur = 70; ctx.shadowOffsetY = 28; }
  rrect(ctx, x, y, w, h, r); ctx.fillStyle = C.panel; ctx.fill(); ctx.restore();
  ctx.save(); rrect(ctx, x, y, w, h, r); ctx.clip(); ctx.imageSmoothingQuality = 'high';
  for (const [im, a] of imgs) if (im && a > 0.02) { ctx.globalAlpha = a; ctx.drawImage(im, src[0], src[1], src[2], src[3], x, y, w, h); }
  ctx.restore();
  if (border) { ctx.save(); rrect(ctx, x + 1, y + 1, w - 2, h - 2, r); ctx.strokeStyle = C.lineStrong; ctx.lineWidth = 2; ctx.stroke(); ctx.restore(); }
}
function zr(img, box, cx, cy, z) {
  const s0 = Math.max(box[2] / img.width, box[3] / img.height) * z, w = box[2] / s0, h = box[3] / s0;
  return [clamp(cx * img.width - w / 2, 0, img.width - w), clamp(cy * img.height - h / 2, 0, img.height - h), w, h];
}
const still = (ctx, img, box, cx, cy, z, o) => card(ctx, [[img, 1]], box, zr(img, box, cx, cy, z), o);

function pill(ctx, str, xr, y, size, bg, fg, o = {}) {
  const { fam = UI, w = 500, rtl = true, padX = 28, h = size * 1.7 } = o;
  const tww = tw(ctx, str, size, fam, w, rtl);
  rrect(ctx, xr - tww - padX * 2, y, tww + padX * 2, h, h / 2); ctx.fillStyle = bg; ctx.fill();
  T(ctx, str, xr - padX, y + h * 0.5 + size * 0.34, size, fg, { fam, w, rtl });
  return tww + padX * 2;
}

// chapter rail: 8 features, first on the right (RTL)
const FEATS = [30, 40, 50, 62, 76, 86, 98, 108];
function rail(ctx, u) {
  if (u < 30 || u >= 108) return;
  const x0 = 96, x1 = 1824, n = 7, gap = 14, sw = (x1 - x0 - gap * (n - 1)) / n, y = 1016;
  for (let i = 0; i < n; i++) {
    const x = x1 - (i + 1) * sw - i * gap;
    rrect(ctx, x, y, sw, 6, 3); ctx.fillStyle = C.line; ctx.fill();
    const p = prog(u, FEATS[i], FEATS[i + 1]);
    if (p > 0) { rrect(ctx, x + sw * (1 - p), y, sw * p, 6, 3); ctx.fillStyle = C.accent; ctx.fill(); }
  }
}

// ------------------------------------------------------------------ scenes
const PROMPT = 'في واحد وراك، اجري، وبعدها موت';
const S = (k) => IMGS[k];
function full(ctx, img, u, a, s0 = 0.94) {              // a real 1920x1080 capture at native size: settles from s0 to 1.0 (never above 1.0x)
  const s = lerp(s0, 1, E.outCubic(prog(u, a, a + 1.6)));
  ctx.save(); ctx.translate(W / 2, H / 2); ctx.scale(s, s); ctx.translate(-W / 2, -H / 2);
  ctx.shadowColor = 'rgba(0,0,0,.5)'; ctx.shadowBlur = 50; ctx.imageSmoothingQuality = 'high'; ctx.drawImage(img, 0, 0, W, H); ctx.restore();
}
function scrim(ctx, side, a = 0.9, span = 0.6) {         // dark gradient so type sits on the real UI
  const g = side === 'b' ? ctx.createLinearGradient(0, H, 0, H * 0.38) : ctx.createLinearGradient(side === 'r' ? W : 0, 0, side === 'r' ? W * (1 - span) : W * span, 0);
  g.addColorStop(0, `rgba(29,27,25,${a})`); g.addColorStop(side === 'b' ? 0.55 : 0.4, `rgba(29,27,25,${a * 0.82})`); g.addColorStop(1, 'rgba(29,27,25,0)'); ctx.fillStyle = g; ctx.fillRect(0, 0, W, H);
}
function backdrop(ctx, sec, a = 0.82) {                  // the real app, dimmed, behind a 1:1 detail card
  ctx.imageSmoothingQuality = 'high'; for (const [im, al] of vf('h', sec)) if (im && al > 0.02) { ctx.globalAlpha = al; ctx.drawImage(im, 0, 0, W, H); }
  ctx.globalAlpha = 1; ctx.fillStyle = `rgba(29,27,25,${a})`; ctx.fillRect(0, 0, W, H);
}
function s1(ctx, u) {                                   // 0-6 hook: the hero prompt typed (real recording, 1:1 crop)
  fill(ctx, C.bg);
  const sec = lerp(REC.typeStart, REC.typeEnd, prog(u, 0, 5.2)), [cx, cy, cw, ch] = REC.hookCrop;
  backdrop(ctx, sec, 0.84);
  const k = springU(u, -0.5, SPRING.gentle), sc = 0.94 + 0.06 * k, bw = cw * sc, bh = ch * sc;
  card(ctx, vf('h', sec), [W / 2 - bw / 2, 130, bw, bh], [cx, cy, cw, ch]);
  ctx.save(); rrect(ctx, XR - 64, 78, 64, 64, 14); ctx.clip(); ctx.drawImage(LOGO, XR - 64, 78, 64, 64); ctx.restore();
  T(ctx, 'Blazma Animation', XR - 84, 124, 40, C.text2, { fam: UI, w: 400, rtl: false });
  const n = [...PROMPT].length, shown = Math.floor(n * E.inOutSine(prog(u, 0.2, 4.4)) + 0.0001);
  const pre = [...PROMPT].slice(0, shown).join(''), size = 128, xr = 1750, y = 720;
  if (pre) T(ctx, pre, xr, y, size, C.text);
  const cxr = xr - (pre ? tw(ctx, pre, size) : 0) - 10;
  if (Math.floor(u * 2) % 2 === 0 || shown < n) { ctx.fillStyle = C.accent; ctx.fillRect(cxr - 8, y - size * 0.86, 8, size * 1.1); }
  slam(ctx, u, 4.4, 'اكتب بالعربي، والشخصية تتحرك', xr, 900, 56, C.text2, { fam: UI, w: 400, maxW: 1500, delay: 0.3, cfg: SPRING.snappy });
}
function s2(ctx, u) {                                   // 6-12 problem
  fill(ctx, C.bg); ctx.drawImage(BLUR, 0, 0, W, H); ctx.fillStyle = 'rgba(29,27,25,.80)'; ctx.fillRect(0, 0, W, H);
  slam(ctx, u, 6.1, 'صناعة الحركة صعبة', XR, 470, 176, C.text, { maxW: 1740, delay: 0.38 });
  slam(ctx, u, 8.2, 'وتاخذ وقت', XR, 740, 232, C.accent, { maxW: 1740, delay: 0.45 });
  bar(ctx, u, 9.6, XR, 800, 260, 10);
}
function s3(ctx, u) {                                   // 12-18 plan appears, then generation (real recording at 1:1)
  fill(ctx, C.bg);
  const sec = lerp(REC.planStart, REC.planEnd, prog(u, 12, 18)), [cx, cy, cw, ch] = REC.planCrop, k = springU(u, 12, SPRING.gentle);
  backdrop(ctx, sec, 0.86);
  card(ctx, vf('h', sec), [(W - cw) / 2, 90 + (1 - k) * 50, cw, ch], [cx, cy, cw, ch]);
  slam(ctx, u, 12.2, 'يفهم الوصف ويخطّط', (W + cw) / 2, 690, 100, C.text, { maxW: 1200 });
  ['في واحد وراك', 'اجري', 'موت'].forEach((s, i) => {
    const kk = springU(u, 13 + i * 0.7, SPRING.snappy); if (kk <= 0.001) return;
    const w = 400, x = (W + cw) / 2 - i * (w + 24) + (1 - kk) * 500, y = 765;
    rrect(ctx, x - w, y, w, 76, 38); ctx.fillStyle = C.raised; ctx.fill();
    ctx.beginPath(); ctx.arc(x - 38, y + 38, 26, 0, 6.2832); ctx.fillStyle = C.accent; ctx.fill();
    T(ctx, String(i + 1), x - 38, y + 52, 38, C.onAccent, { fam: UI, w: 600, align: 'center', rtl: false });
    T(ctx, s, x - 84, y + 53, 44, C.text, { fam: UI, w: 400 });
  });
  const gp = prog(u, 15.4, 17.9), bx = (W + cw) / 2;
  if (u >= 15.2) { slam(ctx, u, 15.2, 'جاري التوليد', bx, 925, 40, C.text2, { fam: UI, w: 400, maxW: 640, cfg: SPRING.snappy });
    rrect(ctx, bx - cw, 950, cw, 14, 7); ctx.fillStyle = C.line; ctx.fill(); rrect(ctx, bx - cw * gp, 950, cw * gp, 14, 7); ctx.fillStyle = C.accent; ctx.fill(); }
}
function heroSec(u) { return kf(u, [[18, REC.playStart + 1.0], [24.2, REC.playStart + 5.5], [30, REC.playStart + 7.7]], E.linear); }
function s4(ctx, u) {                                   // 18-30 hero: real Kimodo motion, runs then falls (native 1080p footage)
  fill(ctx, C.bg);
  ctx.save(); if (u > 24.6) shake(ctx, u, 24.6, 12, 5, 0.2); ctx.imageSmoothingQuality = 'high';
  for (const [im, a] of vf('h', heroSec(u))) if (im && a > 0.02) { ctx.globalAlpha = a; ctx.drawImage(im, 0, 0, W, H); }
  ctx.restore(); ctx.globalAlpha = 1; scrim(ctx, 'b', 0.85);
  const caps = [[18.3, 21.5, 'في واحد وراك'], [21.5, 24.6, 'اجري']];
  for (const [a, b, s] of caps) if (u >= a && u < b) slam(ctx, u, a, s, XR, 960, 120, C.text, { maxW: 900 });
  if (u >= 25.25) { const q = 0.05 * wobble(u - 25.25, 1.6, 4); ctx.save(); ctx.translate(XR, 960); ctx.scale(1 + q, 1 - q); ctx.translate(-XR, -960);
    slam(ctx, u, 25.25, 'وبعدها موت', XR, 960, 190, C.accent, { maxW: 1400, delay: 0.3 }); ctx.restore(); }
}
function lowerThird(ctx, u, a, title, sub) {            // headline + sub over a scrim, bottom right
  const n = slam(ctx, u, a, title, XR, 880, 150, C.text, { maxW: 1100 });
  bar(ctx, u, a + 0.8, XR, 910 + (n - 1) * 180, 150, 9);
  if (sub) slam(ctx, u, a + 1.1, sub, XR, 985 + (n - 1) * 180, 50, C.text2, { fam: UI, w: 400, maxW: 1100, cfg: SPRING.snappy });
}
function s5(ctx, u) {                                   // 30-40 free camera: full-bleed real capture
  fill(ctx, C.bg); full(ctx, S('cam'), u, 30);
  const km = springU(u, 35, SPRING.bouncy); if (km > 0.001) { const w = 270 * km, h = 328 * km; card(ctx, [[S('camMenu'), 1]], [W - w - 520, 140, w, h], [0, 0, 270, 328], { r: 10 }); }
  const ks = springU(u, 37, SPRING.bouncy); if (ks > 0.001) { const w = 420 * ks, h = 270 * ks; card(ctx, [[S('camSet'), 1]], [W - w - 520, 520, w, h], [0, 0, 420, 270], { r: 10 }); }
  scrim(ctx, 'b', 0.92); lowerThird(ctx, u, 30.2, 'كاميرا حرة', 'حلّق حول المشهد من أي زاوية');
}
function tag(ctx, str, x, y, on, size = 44) {
  const w = tw(ctx, str, size, UI, 600) + 56;
  rrect(ctx, x - w, y, w, size * 1.7, size * 0.85); ctx.fillStyle = on ? C.accent : C.raised; ctx.fill();
  T(ctx, str, x - 28, y + size * 1.2, size, on ? C.onAccent : C.text, { fam: UI, w: 600 });
}
function s6(ctx, u) {                                   // 40-50 balance: full-bleed before, divider sweeps to reveal after
  fill(ctx, C.bg); const p = E.inOutCubic(prog(u, 43, 47.2)), dx = W * p;
  ctx.drawImage(S('before'), 0, 0, W, H);
  if (p > 0) { ctx.save(); ctx.beginPath(); ctx.rect(0, 0, dx, H); ctx.clip(); ctx.drawImage(S('after'), 0, 0, W, H); ctx.restore();
    ctx.fillStyle = C.text; ctx.fillRect(dx - 2, 0, 4, H); ctx.beginPath(); ctx.arc(dx, H / 2, 24, 0, 6.2832); ctx.fillStyle = C.accent; ctx.fill(); }
  scrim(ctx, 'b', 0.92); tag(ctx, p < 0.55 ? 'قبل' : 'بعد', 1824, 90, p >= 0.55, 52);
  lowerThird(ctx, u, 40.2, 'توازن فيزيائي', 'بدل ما ينهار.. يوقف على رجليه');
}
function s7(ctx, u) {                                   // 50-62 combat: push-ins on real captures, then the hit test
  const i = u < 54 ? 0 : u < 58 ? 1 : 2, a = [50, 54, 58][i];
  fill(ctx, C.bg);
  if (i < 2) full(ctx, S(i ? 'combo' : 'hit'), u, a, 0.8);
  else { full(ctx, S('hit'), u, 54, 1); ctx.fillStyle = 'rgba(29,27,25,.68)'; ctx.fillRect(0, 0, W, H);
    const kk = springU(u, 58, SPRING.bouncy), s = 0.9 + 0.1 * kk, w = 600 * s, h = 571 * s; card(ctx, [[S('hitTest'), 1]], [W - w - 300, H / 2 - h / 2 - 20, w, h], [0, 0, 600, 571], { r: 12 }); }
  scrim(ctx, 'b', 0.92);
  lowerThird(ctx, u, 50.2, 'تصادم وضربات', ['صناديق الضرب على الجسم', 'رتّب ضرباتك على الخط الزمني', 'واختبر الإصابة قبل التصدير'][i]);
}
function s8(ctx, u) {                                   // 62-76 AI models: real window on this machine (light tier installed), real device list
  fill(ctx, C.bg);
  const k = springU(u, 62, SPRING.gentle), xr = 1000;
  if (u < 72.5) card(ctx, [[S('mInst'), 1]], [W - 760 - 110, 150 + (1 - k) * 60, 760, 720], [0, 0, 760, 720], { r: 14 });
  else {
    const k1 = Math.max(0.6, springU(u, 72.2, SPRING.bouncy)), k2 = springU(u, 73.0, SPRING.bouncy);
    ctx.save(); ctx.translate(W - 110 - 340, 250 + 78); ctx.scale(k1, k1); ctx.translate(-340, -78); card(ctx, [[S('devSet'), 1]], [0, 0, 680, 155], [0, 245, 680, 155], { r: 12 }); ctx.restore();
    if (k2 > 0.001) { ctx.save(); ctx.translate(W - 110 - 537 - 60, 470); ctx.scale(k2, k2); card(ctx, [[S('devPop'), 1]], [0, 0, 537, 128], [0, 0, 537, 128], { r: 10 }); ctx.restore(); }
  }
  const n = slam(ctx, u, 62.2, 'نماذج الذكاء الاصطناعي', xr, 330, 150, C.text, { maxW: 860 });
  bar(ctx, u, 63, xr, 330 + (n - 1) * 180 + 40, 150, 9);
  slam(ctx, u, 63.4, 'تشتغل على جهازك.. بدون إنترنت', xr, 330 + (n - 1) * 180 + 140, 56, C.text2, { fam: UI, w: 400, maxW: 860, cfg: SPRING.snappy });
  if (u >= 72.5) { const kk = springU(u, 72.5, SPRING.bouncy), q = 0.04 * wobble(u - 72.5, 2, 5); ctx.save(); ctx.translate(xr, 830); ctx.scale(kk * (1 + q), kk * (1 - q)); ctx.translate(-xr, -830);
    const gw = tw(ctx, 'GPU', 190, DISP, 600, false) + 100; rrect(ctx, xr - gw, 700, gw, 250, 44); ctx.fillStyle = C.accent; ctx.fill(); T(ctx, 'GPU', xr - gw / 2, 880, 190, C.onAccent, { align: 'center', rtl: false }); ctx.restore();
    slam(ctx, u, 73.6, 'يدعم كرت الشاشة', xr, 1010, 46, C.text2, { fam: UI, w: 400, maxW: 860, cfg: SPRING.snappy }); }
}
function s9(ctx, u) {                                   // 76-86 export: the dialog rises under a giant headline
  fill(ctx, C.bg);
  slam(ctx, u, 76.2, 'صدّر لمحركات الألعاب', XR, 300, 170, C.text, { maxW: 1500, delay: 0.4 });
  bar(ctx, u, 77.4, XR, 520, 150, 9);
  slam(ctx, u, 78, 'نفس الحركة.. جاهزة في لعبتك', XR, 640, 60, C.text2, { fam: UI, w: 400, maxW: 900, cfg: SPRING.snappy });
  const k = springU(u, 75.6, SPRING.snappy); card(ctx, [[S('exp'), 1]], [260, lerp(1180, 330, k), 600, 665], [0, 0, 600, 665], { r: 14 });
}
function s10(ctx, u) {                                  // 86-98 Claude via MCP (type only; the icon's own bone motif)
  fill(ctx, C.bg);
  const cx = 470, cy = 640, L = 140, k = Math.max(0.35, springU(u, 85.6, SPRING.gentle)); const pts = [[cx - 200, cy + 150]]; let a0 = -0.9;
  for (let j = 0; j < 4; j++) { a0 += 0.34 * Math.sin((u - 86) * 1.1 + j * 0.9) + 0.28; const p = pts[j]; pts.push([p[0] + L * k * Math.cos(a0), p[1] + L * k * Math.sin(a0)]); }
  ctx.lineCap = 'round'; ctx.lineJoin = 'round'; ctx.strokeStyle = C.text; ctx.lineWidth = 24; ctx.beginPath(); pts.forEach((p, i) => (i ? ctx.lineTo(p[0], p[1]) : ctx.moveTo(p[0], p[1]))); ctx.stroke();
  pts.forEach((p, i) => { ctx.beginPath(); ctx.arc(p[0], p[1], i === 4 ? 34 : 26, 0, 6.2832); ctx.fillStyle = i === 4 ? C.accent : C.text; ctx.fill(); });
  for (let i = 0; i < 11; i++) { const kk = springU(u, 87 + i * 0.75, SPRING.bouncy); if (kk <= 0.001) continue; ctx.save(); ctx.translate(130 + i * 76, 910); ctx.rotate(0.785); ctx.scale(kk, kk); ctx.fillStyle = C.accent; ctx.fillRect(-14, -14, 28, 28); ctx.restore(); }
  rrect(ctx, 96, 925, 830, 4, 2); ctx.fillStyle = C.line; ctx.fill();
  slam(ctx, u, 86.2, 'Claude', XR, 440, 210, C.text, { fam: DISP, maxW: 900 });
  slam(ctx, u, 87.4, 'عبر MCP', XR, 620, 128, C.accent, { maxW: 800, delay: 0.4 }); bar(ctx, u, 88.4, XR, 660, 150, 9);
  slam(ctx, u, 89.4, 'كلّمه.. وهو يحرّك لك الشخصية', XR, 770, 52, C.text2, { fam: UI, w: 400, maxW: 760, lh: 1.45, cfg: SPRING.snappy });
}
function s11(ctx, u) {                                  // 98-108 and more: real dialogs/captures, assembled on the beat (all at <= 1.0x)
  fill(ctx, C.bg);
  const tiles = [['tpl', 98, 700, 570, 740, 378], ['hitTest', 100, 96, 90, 600, 571], ['gen', 102, 700, 90, 740, 416]];
  tiles.forEach(([k, a, x, y, dw, dh], j) => { const kk = j === 0 ? Math.max(0.5, springU(u, a - 0.4, SPRING.bouncy)) : springU(u, a, SPRING.bouncy); if (kk <= 0.001) return;
    const img = S(k); ctx.save(); ctx.translate(x + dw / 2, y + dh / 2); ctx.scale(kk, kk); ctx.translate(-dw / 2, -dh / 2); card(ctx, [[img, 1]], [0, 0, dw, dh], [0, 0, img.width, img.height], { r: 12 }); ctx.restore(); });
  headline(ctx, u, 98.3, 'وأكثر بكثير', null, { size: 130, y: 380, maxW: 380 });
}
function s12(ctx, u) {                                  // 108-116 button: the fall again, slow motion
  fill(ctx, C.bg); const sec = REC.playStart + 5.6 + (u - 108) * M.GRID.period * 0.7;
  ctx.imageSmoothingQuality = 'high'; for (const [im, a] of vf('h', sec)) if (im && a > 0.02) { ctx.globalAlpha = a; ctx.drawImage(im, 0, 0, W, H); }
  ctx.globalAlpha = 1; ctx.fillStyle = 'rgba(29,27,25,.6)'; ctx.fillRect(0, 0, W, H);
  slam(ctx, u, 108.2, 'حرّك شخصيتك', XR, 500, 230, C.text, { maxW: 1740, delay: 0.5 });
  slam(ctx, u, 109.6, 'بكلمات', XR, 760, 250, C.accent, { maxW: 1740 });
}
function s13(ctx, u) {                                  // 116-128 end card
  fill(ctx, C.bg);
  const k = springU(u, 115.55, { stiffness: 200, damping: 13 }), s = 460, y = lerp(-500, 310, k);
  ctx.save(); ctx.shadowColor = 'rgba(0,0,0,.5)'; ctx.shadowBlur = 60; ctx.shadowOffsetY = 24; rrect(ctx, 220, y, s, s, 90); ctx.clip(); ctx.drawImage(LOGO, 220, y, s, s); ctx.restore();
  slam(ctx, u, 116.5, 'Blazma Animation', XR, 480, 112, C.text, { fam: DISP, whole: true });
  slam(ctx, u, 117.8, 'حمّله الآن من GitHub', XR, 650, 96, C.accent, { maxW: 1100, delay: 0.3 });
  slam(ctx, u, 119, 'github.com/mr-kateba/blazma-Animation', XR, 780, 40, C.text2, { fam: UI, w: 400, whole: true, cfg: SPRING.snappy });
  const kc = springU(u, 121, SPRING.gentle);                 // Studio Nakhla credit under the icon (their real logo)
  if (kc > 0.001 && NK) { const lw = 200, lh = lw * NK.height / NK.width; ctx.save(); ctx.globalAlpha = Math.min(1, kc * 1.4); ctx.drawImage(NK, 350, 805 + (1 - kc) * 24, lw, lh); ctx.restore();
    slam(ctx, u, 121.4, 'من استوديو نخلة', 350 + (lw + tw(ctx, 'من استوديو نخلة', 36, UI, 400)) / 2, 805 + lh + 52, 36, C.text2, { fam: UI, w: 400, maxW: 700, cfg: SPRING.snappy }); }
  const kp = springU(u, 120, SPRING.bouncy);
  if (kp > 0.001) { ctx.save(); ctx.translate(XR, 940); ctx.scale(kp, kp); ctx.translate(-XR, -940); pill(ctx, 'v0.12', XR, 880, 44, C.accent, C.onAccent, { fam: UI, w: 600, rtl: false }); ctx.restore(); }
}
const SCENES = [[0, 6, s1], [6, 12, s2], [12, 18, s3], [18, 30, s4], [30, 40, s5], [40, 50, s6], [50, 62, s7], [62, 76, s8], [76, 86, s9], [86, 98, s10], [98, 108, s11], [108, 116, s12], [116, 128, s13]];
let IMGS = {};
const HITS = [
  [0, 'open', 'impact', { gain: 0.45 }], [0.2, 'typing', 'type', { len: 2.0, gain: 0.5 }],
  [6.2, 'word', 'click'], [6.58, 'word', 'click'], [6.96, 'word', 'click'], [8.2, 'وتاخذ وقت', 'thud'], [9.7, 'bar', 'tick', { gain: 0.5 }],
  [12, 'drop riser', 'riser', { len: 3.4 }], [12, 'drop', 'impact'],
  [13, 'step 1', 'pop', { pitch: 'D5' }], [13.7, 'step 2', 'pop', { pitch: 'F5' }], [14.4, 'step 3', 'pop', { pitch: 'A5' }], [15.2, 'generating', 'tick', { gain: 0.55 }], [17.9, 'done', 'bell', { pitch: 'D6' }],
  [18, 'hero in', 'whoosh', { len: 0.4 }], [18.3, 'caption', 'click'], [21.5, 'caption', 'click'], [25.25, 'موت', 'impact'],
  [30, 'cut', 'click'], [30.3, 'title', 'pop', { pitch: 'D5' }], [35, 'menu', 'pop', { pitch: 'F5' }], [37, 'settings', 'tick'],
  [40, 'cut', 'click'], [40.35, 'title', 'pop', { pitch: 'D5', gain: 0.55 }], [43, 'sweep', 'whoosh', { len: 0.5 }], [47.2, 'balanced', 'bell', { pitch: 'D6' }],
  [50, 'hit', 'thud'], [50.35, 'title', 'pop', { pitch: 'D5', gain: 0.55 }], [54, 'combo', 'click'], [58, 'hit test', 'pop', { pitch: 'A5' }],
  [62, 'cut', 'click'], [62.35, 'title', 'pop', { pitch: 'D5', gain: 0.55 }], [66, 'dialog', 'click'], [68, 'light tier', 'tick'], [72.5, 'devices', 'bell', { pitch: 'D6' }], [72.7, 'GPU', 'impact', { gain: 0.5 }],
  [76, 'dialog in', 'whoosh', { len: 0.45 }], [76.2, 'title', 'pop', { pitch: 'D5' }], [77.6, 'dialog lands', 'thud', { gain: 0.8 }],
  [86, 'cut', 'sub', { gain: 0.4 }], [86.3, 'Claude', 'pop', { pitch: 'D5' }], [87.6, 'MCP', 'pop', { pitch: 'A5', gain: 0.55 }], [89.6, 'sub', 'tick', { gain: 0.5 }], [92, 'keys', 'bell', { pitch: 'F6' }],
  [98, 'tile', 'pop', { pitch: 'D5' }], [100, 'tile', 'pop', { pitch: 'F5' }], [102, 'tile', 'pop', { pitch: 'A5' }], [104, 'tile', 'pop', { pitch: 'D6' }], [106, 'riser', 'riser', { len: 1.8 }],
  [108, 'close', 'sub', { gain: 0.45 }], [108.3, 'حرّك', 'pop', { pitch: 'D5' }], [109.7, 'بكلمات', 'pop', { pitch: 'A5' }],
  [116, 'end card', 'impact'], [116.7, 'logo lands', 'thud', { gain: 0.9 }], [118, 'GitHub', 'bell', { pitch: 'D6', gain: 0.55 }], [120, 'v0.12', 'pop', { pitch: 'A5' }], [121.2, 'credit', 'tick', { gain: 0.5 }],
];

// A one-off blurred backdrop for the "problem" shot: the real sequence screenshot, downsampled then stretched
function makeBlur(img) {
  const s = document.createElement('canvas'); s.width = 96; s.height = 60; s.getContext('2d').drawImage(img, 0, 0, 96, 60);
  const b = document.createElement('canvas'); b.width = 480; b.height = 300; const c = b.getContext('2d'); c.imageSmoothingQuality = 'high'; c.drawImage(s, 0, 0, 480, 300);
  const o = document.createElement('canvas'); o.width = W; o.height = H; const oc = o.getContext('2d'); oc.imageSmoothingQuality = 'high'; oc.drawImage(b, 0, 0, W, H); return o;
}

M.film({
  fonts: [font(100, 600, DISP), font(40, 400, UI), font(40, 600, UI)],
  images: {
    cam: 'assets/s/p10-ar-dark-camera-viewport.png', camMenu: 'assets/s/p10-ar-dark-camera-view-menu.png', camSet: 'assets/s/p10-ar-dark-camera-settings.png',
    before: 'assets/s/p6-ar-dark-unbalanced-before.png', after: 'assets/s/p6-ar-dark-balanced-after.png',
    seq: 'assets/s/p8-ar-dark-sequence.png', tpl: 'assets/s/p8-ar-dark-templates.png', ret: 'assets/s/p8-ar-dark-retarget.png',
    hit: 'assets/s/p9-ar-dark-overlay-hit.png', combo: 'assets/s/p9-ar-dark-combo-editor.png', hitTest: 'assets/s/p9-ar-dark-hit-test-dialog.png',
    mInst: 'assets/s/reel-ar-models-installed.png', devSet: 'assets/s/reel-ar-ai-settings.png', devPop: 'assets/s/reel-ar-device-popup.png',
    exp: 'assets/s/p4-ar-dark-export-dialog.png', gen: 'assets/s/p7-ar-dark-generated.png', logo: 'assets/logo.png', nk: 'assets/nakhla/nakhla-logo-ondark.png',
  },
  hits: HITS, samples: () => 8,
  async init() { await loadSeq(); },
  draw(ctx, u, t, IMG) {
    IMGS = IMG; LOGO = IMG.logo; NK = IMG.nk; if (!BLUR) BLUR = makeBlur(IMG.seq);
    const i = SCENES.findIndex(([a, b]) => u < b), idx = i < 0 ? SCENES.length - 1 : i;
    const fn = SCENES[idx][2];
    if (u > 11.35 && u < 12.75) {
      const p = E.inOutCubic(prog(u, 11.35, 12.75)), bw = 1100, c = lerp(W + bw / 2, -bw / 2, p);
      ctx.save(); ctx.beginPath(); ctx.rect(0, 0, Math.max(0, c - bw / 2), H); ctx.clip(); s2(ctx, Math.min(u, 11.99)); ctx.restore();
      ctx.save(); ctx.beginPath(); ctx.rect(Math.min(W, c + bw / 2), 0, W, H); ctx.clip(); s3(ctx, Math.max(u, 12)); ctx.restore();
      ctx.fillStyle = C.accent; ctx.fillRect(c - bw / 2, 0, bw, H);
    } else fn(ctx, u);
    rail(ctx, u);
  },
});
