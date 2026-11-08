// Blazma Animation: TikTok cut (9:16, 1080x1920). Pure function of time; timing in beats (u) on beats.json.
// Safe area for ALL text: x 60..960, y 220..1500 (TikTok UI: top ~220, bottom ~420, right ~120). Footage may bleed outside it.
import * as M from './lib/motion.js';
const { W, H, E, prog, lerp, clamp, springU, SPRING, wobble, kf, font, fill, rrect, shake } = M;
const C = {
  bg: '#1d1b19', panel: '#252320', panel2: '#2c2a26', raised: '#34312c', line: '#3b3833', lineStrong: '#4a463f',
  text: '#ece7df', text2: '#b4ada2', text3: '#857e73', accent: '#c27a3e', onAccent: '#1d1b19',
};
const DISP = 'Display', UI = 'UI';
const XR = 960, XL = 60;               // text column: right edge (kept 120 px off the right UI column) and left margin
const SAFE_Y = [220, 1500];

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
let REC = null, LOGO = null, NK = null;
const SQ = { h: {}, n: {} }, FPS = { h: 60, n: 60 };
async function loadSeq() {
  REC = await (await fetch('assets/rec.json')).json();
  const jobs = [], want = { h: [[3.4, 10.1], [12.0, 22.4]], n: [[0, 3.5]] };
  for (const k in want) for (const [a, b] of want[k]) for (let i = Math.floor(a * 60); i <= Math.ceil(b * 60) + 2; i++) {
    if (SQ[k][i] || i < 1 || i > (k === 'h' ? REC.frames : 210)) continue;
    const im = new Image(); im.src = `assets/seq/${k}/${String(i).padStart(k === 'h' ? 5 : 5, '0')}.jpg`;
    jobs.push(new Promise((r) => { im.onload = r; im.onerror = r; })); SQ[k][i] = im;
  }
  await Promise.all(jobs);
}
function vf(k, sec) {                                   // footage frame at `sec`, blended with the next for slow motion
  const f = Math.max(0, sec) * FPS[k], a = Math.floor(f);
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

// ------------------------------------------------------------------ scenes (beats; the Nakhla intro takes 0-8)
const PROMPT = 'في واحد وراك، اجري، وبعدها موت';
const S = (k) => IMGS[k];
function full(ctx, img, u, a, s0 = 0.94) {              // a real 1080x1920 capture at native size, settling from s0 to 1.0 (never above 1.0x)
  const s = lerp(s0, 1, E.outCubic(prog(u, a, a + 1.4)));
  ctx.save(); ctx.translate(W / 2, H / 2); ctx.scale(s, s); ctx.translate(-W / 2, -H / 2); ctx.imageSmoothingQuality = 'high'; ctx.drawImage(img, 0, 0, W, H); ctx.restore();
}
function scrim(ctx, a = 0.92, from = 0.58) {            // bottom gradient so type sits on the real UI
  const g = ctx.createLinearGradient(0, H, 0, H * from); g.addColorStop(0, `rgba(29,27,25,${a})`); g.addColorStop(0.5, `rgba(29,27,25,${a * 0.85})`); g.addColorStop(1, 'rgba(29,27,25,0)');
  ctx.fillStyle = g; ctx.fillRect(0, 0, W, H);
}
function backdrop(ctx, sec, a = 0.84) {
  fill(ctx, C.bg); ctx.imageSmoothingQuality = 'high';
  for (const [im, al] of vf('h', sec)) if (im && al > 0.02) { ctx.globalAlpha = al; ctx.drawImage(im, 0, 0, W, H); }
  ctx.globalAlpha = 1; ctx.fillStyle = `rgba(29,27,25,${a})`; ctx.fillRect(0, 0, W, H);
}
function lowerThird(ctx, u, a, title, sub) {            // headline + sub, bottom of the safe area
  const n = slam(ctx, u, a, title, XR, 1290, 128, C.text, { maxW: 900 });
  bar(ctx, u, a + 0.8, XR, 1290 + (n - 1) * 154 + 34, 140, 9);
  if (sub) slam(ctx, u, a + 1.1, sub, XR, 1290 + (n - 1) * 154 + 120, 46, C.text2, { fam: UI, w: 400, maxW: 900, cfg: SPRING.snappy });
}
function tag(ctx, str, xr, y, on, size = 46) {
  const w = tw(ctx, str, size, UI, 600) + 56;
  rrect(ctx, xr - w, y, w, size * 1.7, size * 0.85); ctx.fillStyle = on ? C.accent : C.raised; ctx.fill();
  T(ctx, str, xr - 28, y + size * 1.2, size, on ? C.onAccent : C.text, { fam: UI, w: 600 });
}
// ---- 0-8 studio intro: Studio Nakhla's own footage (first 3.5 s, before the partner logos), cropped 1080x1080 at 1.0x, background extended
function s0(ctx, u) {
  const sec = Math.min(u * M.GRID.period, 3.5), f = Math.floor(sec * 60) + 1, im = SQ.n[Math.min(f, 210)] || SQ.n[210];
  fill(ctx, '#06140f'); if (!im) return;
  const y0 = (H - 1080) / 2;
  ctx.drawImage(im, 420, 0, 1080, 1080, 0, y0, 1080, 1080);
  ctx.drawImage(im, 420, 0, 1080, 3, 0, 0, 1080, y0);                       // its own edge colour, carried up and down (no scaling of the logo)
  ctx.drawImage(im, 420, 1077, 1080, 3, 0, y0 + 1080, 1080, H - y0 - 1080);
}
function s1(ctx, u) {                                   // 8-14 hook
  const sec = lerp(REC.typeStart, REC.typeEnd, prog(u, 9.5, 13)), [cx, cy, cw, ch] = REC.hookCrop;
  backdrop(ctx, sec, 0.93);
  const n = slam(ctx, u, 8.1, 'اكتب.. وشوف الحركة', XR, 360, 118, C.text, { maxW: 900 });
  bar(ctx, u, 9, XR, 360 + (n - 1) * 142 + 36, 150, 9);
  const k = springU(u, 9.4, SPRING.gentle), sc = 0.94 + 0.06 * k;
  card(ctx, vf('h', sec), [W / 2 - cw * sc / 2, 650, cw * sc, ch * sc], [cx, cy, cw, ch]);
  const L = ['في واحد وراك،', 'اجري، وبعدها موت'], total = [...PROMPT].length - 0, shown = Math.floor(total * E.inOutSine(prog(u, 9.6, 12.8)) + 0.0001);
  let left = shown;
  L.forEach((line, i) => { const ch2 = [...line], take = Math.min(ch2.length, Math.max(0, left)); left -= ch2.length;
    const pre = ch2.slice(0, take).join(''), y = 980 + i * 120, size = 96;
    if (pre) T(ctx, pre, XR, y, size, C.text);
    if (take > 0 && take < ch2.length + 0.5 && (left <= 0) && (Math.floor(u * 2) % 2 === 0 || shown < total)) { ctx.fillStyle = C.accent; ctx.fillRect(XR - (pre ? tw(ctx, pre, size) : 0) - 18, y - size * 0.86, 8, size * 1.1); } });
  slam(ctx, u, 13, 'اكتب بالعربي، والشخصية تتحرك', XR, 1300, 46, C.text2, { fam: UI, w: 400, maxW: 900, cfg: SPRING.snappy });
}
function s2(ctx, u) {                                   // 14-18 plan
  const sec = lerp(REC.planStart, REC.planEnd, prog(u, 14, 18)), [cx, cy, cw, ch] = REC.planCrop, k = springU(u, 14, SPRING.gentle);
  backdrop(ctx, sec, 0.93);
  slam(ctx, u, 14.1, 'يفهم الوصف ويخطّط', XR, 330, 100, C.text, { maxW: 900 });
  card(ctx, vf('h', sec), [W / 2 - cw / 2, 420 + (1 - k) * 50, cw, ch], [cx, cy, cw, ch]);
  ['في واحد وراك', 'اجري', 'موت'].forEach((s, i) => {
    const kk = springU(u, 15 + i * 0.8, SPRING.snappy); if (kk <= 0.001) return;
    const x = XR + (1 - kk) * 500, y = 930 + i * 100, w = 640;
    rrect(ctx, x - w, y, w, 80, 40); ctx.fillStyle = C.raised; ctx.fill();
    ctx.beginPath(); ctx.arc(x - 40, y + 40, 28, 0, 6.2832); ctx.fillStyle = C.accent; ctx.fill();
    T(ctx, String(i + 1), x - 40, y + 54, 40, C.onAccent, { fam: UI, w: 600, align: 'center', rtl: false });
    T(ctx, s, x - 90, y + 56, 46, C.text, { fam: UI, w: 400 });
  });
  const gp = prog(u, 16.6, 17.9);
  if (u >= 16.5) { slam(ctx, u, 16.5, 'جاري التوليد', XR, 1300, 40, C.text2, { fam: UI, w: 400, maxW: 640, cfg: SPRING.snappy });
    rrect(ctx, 60, 1325, 900, 14, 7); ctx.fillStyle = C.line; ctx.fill(); rrect(ctx, XR - 900 * gp, 1325, 900 * gp, 14, 7); ctx.fillStyle = C.accent; ctx.fill(); }
}
function heroSec(u) { return kf(u, [[18, REC.playStart + 0.9], [26, REC.playStart + 5.9], [30, REC.playStart + 7.7]], E.linear); }
function s3(ctx, u) {                                   // 18-30 hero: real Kimodo run, then the fall
  fill(ctx, C.bg);
  ctx.save(); if (u > 26) shake(ctx, u, 26, 14, 5, 0.2); ctx.imageSmoothingQuality = 'high';
  for (const [im, a] of vf('h', heroSec(u))) if (im && a > 0.02) { ctx.globalAlpha = a; ctx.drawImage(im, 0, 0, W, H); }
  ctx.restore(); ctx.globalAlpha = 1; scrim(ctx, 0.9, 0.62);
  for (const [a, b, s] of [[18.4, 22, 'في واحد وراك'], [22, 26, 'اجري']]) if (u >= a && u < b) slam(ctx, u, a, s, XR, 1400, 110, C.text, { maxW: 900 });
  if (u >= 26) { const q = 0.05 * wobble(u - 26, 1.6, 4); ctx.save(); ctx.translate(XR, 1300); ctx.scale(1 + q, 1 - q); ctx.translate(-XR, -1300);
    slam(ctx, u, 26, 'وبعدها موت', XR, 1330, 150, C.accent, { maxW: 900, delay: 0.3 }); ctx.restore(); }
}
function s4(ctx, u) {                                   // 30-38 free camera
  fill(ctx, C.bg); full(ctx, S('cam'), u, 30);
  const km = springU(u, 33, SPRING.bouncy); if (km > 0.001) { const w = 270 * km, h = 328 * km; card(ctx, [[S('camMenu'), 1]], [W - w - 140, 330, w, h], [0, 0, 270, 328], { r: 10 }); }
  const ks = springU(u, 35.5, SPRING.bouncy); if (ks > 0.001) { const w = 420 * ks, h = 270 * ks; card(ctx, [[S('camSet'), 1]], [60, 560, w, h], [0, 0, 420, 270], { r: 10 }); }
  scrim(ctx, 0.94, 0.6); lowerThird(ctx, u, 30.3, 'كاميرا حرة', 'حلّق حول المشهد من أي زاوية');
}
function s5(ctx, u) {                                   // 38-48 balance: before, a divider sweeps down to reveal after
  fill(ctx, C.bg); const p = E.inOutCubic(prog(u, 41, 45)), dy = H * p;
  ctx.drawImage(S('before'), 0, 0, W, H);
  if (p > 0) { ctx.save(); ctx.beginPath(); ctx.rect(0, 0, W, dy); ctx.clip(); ctx.drawImage(S('after'), 0, 0, W, H); ctx.restore();
    ctx.fillStyle = C.text; ctx.fillRect(0, dy - 2, W, 4); ctx.beginPath(); ctx.arc(W / 2, dy, 24, 0, 6.2832); ctx.fillStyle = C.accent; ctx.fill(); }
  scrim(ctx, 0.94, 0.6); tag(ctx, p < 0.5 ? 'قبل' : 'بعد', XR, 250, p >= 0.5, 50);
  lowerThird(ctx, u, 38.3, 'توازن فيزيائي', 'بدل ما ينهار.. يوقف على رجليه');
}
function s6(ctx, u) {                                   // 48-58 combat
  const i = u < 52 ? 0 : u < 55.5 ? 1 : 2, a = [48, 52, 55.5][i];
  fill(ctx, C.bg);
  if (i < 2) full(ctx, S(i ? 'combo' : 'hit'), u, a, 0.9);
  else { full(ctx, S('hit'), u, 52, 1); ctx.fillStyle = 'rgba(29,27,25,.7)'; ctx.fillRect(0, 0, W, H);
    const kk = springU(u, 55.5, SPRING.bouncy), s = 0.9 + 0.1 * kk, w = 600 * s, h = 571 * s; card(ctx, [[S('hitTest'), 1]], [W / 2 - w / 2, 480, w, h], [0, 0, 600, 571], { r: 12 }); }
  scrim(ctx, 0.94, 0.6);
  lowerThird(ctx, u, 48.3, 'تصادم وضربات', ['صناديق الضرب على الجسم', 'رتّب ضرباتك على الخط الزمني', 'واختبر الإصابة قبل التصدير'][i]);
}
function s7(ctx, u) {                                   // 58-68 AI on your device, GPU-ready (real models window + real device list)
  fill(ctx, C.bg);
  const k = springU(u, 58, SPRING.gentle);
  const n = slam(ctx, u, 58.2, 'ذكاء اصطناعي على جهازك', XR, 330, 112, C.text, { maxW: 900 });
  bar(ctx, u, 59, XR, 330 + (n - 1) * 134 + 34, 140, 9);
  if (u < 63.5) card(ctx, [[S('mInst'), 1]], [(W - 760) / 2 - 20, 600 + (1 - k) * 60, 760, 720], [0, 0, 760, 720], { r: 14 });
  else {
    const k1 = Math.max(0.6, springU(u, 63.2, SPRING.bouncy)), k2 = springU(u, 64.2, SPRING.bouncy);
    ctx.save(); ctx.translate(W / 2 - 20, 690); ctx.scale(k1, k1); ctx.translate(-340, -78); card(ctx, [[S('devSet'), 1]], [0, 0, 680, 155], [0, 245, 680, 155], { r: 12 }); ctx.restore();
    if (k2 > 0.001) { ctx.save(); ctx.translate(W / 2 - 20 - 268, 820); ctx.scale(k2, k2); card(ctx, [[S('devPop'), 1]], [0, 0, 537, 128], [0, 0, 537, 128], { r: 10 }); ctx.restore(); }
    const kk = springU(u, 63.5, SPRING.bouncy), q = 0.04 * wobble(u - 63.5, 2, 5); ctx.save(); ctx.translate(XR, 1150); ctx.scale(kk * (1 + q), kk * (1 - q)); ctx.translate(-XR, -1150);
    const gw = tw(ctx, 'GPU', 160, DISP, 600, false) + 90; rrect(ctx, XR - gw, 1040, gw, 210, 40); ctx.fillStyle = C.accent; ctx.fill(); T(ctx, 'GPU', XR - gw / 2, 1190, 160, C.onAccent, { align: 'center', rtl: false }); ctx.restore();
    slam(ctx, u, 64.6, 'يدعم كرت الشاشة', XR, 1360, 46, C.text2, { fam: UI, w: 400, maxW: 900, cfg: SPRING.snappy });
  }
}
function s8(ctx, u) {                                   // 68-74 button: the fall again, slow motion
  fill(ctx, C.bg); const sec = REC.playStart + 5.6 + (u - 68) * M.GRID.period * 0.7;
  ctx.imageSmoothingQuality = 'high'; for (const [im, a] of vf('h', sec)) if (im && a > 0.02) { ctx.globalAlpha = a; ctx.drawImage(im, 0, 0, W, H); }
  ctx.globalAlpha = 1; ctx.fillStyle = 'rgba(29,27,25,.6)'; ctx.fillRect(0, 0, W, H);
  slam(ctx, u, 68.2, 'حرّك شخصيتك', XR, 640, 150, C.text, { maxW: 900, delay: 0.5 });
  slam(ctx, u, 69.6, 'بكلمات', XR, 900, 190, C.accent, { maxW: 900 });
}
function s9(ctx, u) {                                   // 74-82 end card + Studio Nakhla credit (their real logo)
  fill(ctx, C.bg);
  const k = springU(u, 73.55, { stiffness: 200, damping: 13 }), s = 300, y = lerp(-340, 300, k);
  ctx.save(); ctx.shadowColor = 'rgba(0,0,0,.5)'; ctx.shadowBlur = 50; ctx.shadowOffsetY = 20; rrect(ctx, (W - s) / 2, y, s, s, 62); ctx.clip(); ctx.drawImage(LOGO, (W - s) / 2, y, s, s); ctx.restore();
  slam(ctx, u, 74.5, 'Blazma Animation', (W + tw(ctx, 'Blazma Animation', 84, DISP, 600, false)) / 2, 720, 84, C.text, { fam: DISP, whole: true });
  slam(ctx, u, 75.8, 'حمّله من GitHub', XR, 880, 112, C.accent, { maxW: 900, delay: 0.3 });
  slam(ctx, u, 77, 'github.com/mr-kateba/blazma-Animation', (W + tw(ctx, 'github.com/mr-kateba/blazma-Animation', 34, UI, 400, false)) / 2, 1010, 34, C.text2, { fam: UI, w: 400, whole: true, cfg: SPRING.snappy });
  const kp = springU(u, 77.5, SPRING.bouncy);
  if (kp > 0.001) { ctx.save(); ctx.translate(W / 2, 1090); ctx.scale(kp, kp); ctx.translate(-W / 2, -1090); pill(ctx, 'v0.12', (W + 150) / 2, 1065, 40, C.accent, C.onAccent, { fam: UI, w: 600, rtl: false }); ctx.restore(); }
  const kc = springU(u, 78.5, SPRING.gentle);
  if (kc > 0.001 && NK) { const lw = 250, lh = lw * NK.height / NK.width; ctx.save(); ctx.globalAlpha = Math.min(1, kc * 1.4); ctx.drawImage(NK, (W - lw) / 2, 1215 + (1 - kc) * 30, lw, lh); ctx.restore();
    slam(ctx, u, 79, 'من استوديو نخلة', (W + tw(ctx, 'من استوديو نخلة', 44, UI, 400)) / 2, 1215 + lh + 70, 44, C.text2, { fam: UI, w: 400, maxW: 900, cfg: SPRING.snappy }); }
}
const SCENES = [[0, 8, s0], [8, 14, s1], [14, 18, s2], [18, 30, s3], [30, 38, s4], [38, 48, s5], [48, 58, s6], [58, 68, s7], [68, 74, s8], [74, 82, s9]];
let IMGS = {};
const HITS = [
  [8, 'drop riser', 'riser', { len: 2.4 }], [8, 'drop', 'impact'],
  [8.1, 'headline', 'pop', { pitch: 'D5' }], [9.4, 'dialog', 'click'], [9.6, 'typing', 'type', { len: 2.4, gain: 0.5 }],
  [14.1, 'title', 'pop', { pitch: 'D5' }], [15, 'step 1', 'pop', { pitch: 'D5' }], [15.8, 'step 2', 'pop', { pitch: 'F5' }], [16.6, 'step 3', 'pop', { pitch: 'A5' }], [17.9, 'done', 'bell', { pitch: 'D6' }],
  [18, 'hero in', 'whoosh', { len: 0.4 }], [18.4, 'caption', 'click'], [22, 'caption', 'click'], [26, 'موت', 'impact'],
  [30, 'cut', 'click'], [30.35, 'title', 'pop', { pitch: 'D5', gain: 0.55 }], [33, 'menu', 'pop', { pitch: 'F5' }], [35.5, 'settings', 'tick'],
  [38, 'cut', 'click'], [38.35, 'title', 'pop', { pitch: 'D5', gain: 0.55 }], [41, 'sweep', 'whoosh', { len: 0.5 }], [44.5, 'balanced', 'bell', { pitch: 'D6' }],
  [48, 'hit', 'thud'], [48.35, 'title', 'pop', { pitch: 'D5', gain: 0.55 }], [52, 'combo', 'click'], [55.5, 'hit test', 'pop', { pitch: 'A5' }],
  [58, 'cut', 'click'], [58.35, 'title', 'pop', { pitch: 'D5', gain: 0.55 }], [63.5, 'devices', 'bell', { pitch: 'D6' }], [63.7, 'GPU', 'impact', { gain: 0.5 }],
  [68, 'close', 'sub', { gain: 0.45 }], [68.3, 'حرّك', 'pop', { pitch: 'D5' }], [69.7, 'بكلمات', 'pop', { pitch: 'A5' }],
  [74, 'end card', 'impact'], [74.7, 'logo lands', 'thud', { gain: 0.9 }], [76, 'GitHub', 'bell', { pitch: 'D6', gain: 0.55 }], [77.5, 'v0.12', 'pop', { pitch: 'A5' }], [78.8, 'credit', 'tick', { gain: 0.5 }],
];

M.film({
  fonts: [font(100, 600, DISP), font(40, 400, UI), font(40, 600, UI)],
  images: {
    cam: 'assets/v/p10-ar-dark-camera-viewport.png', camMenu: 'assets/v/p10-ar-dark-camera-view-menu.png', camSet: 'assets/v/p10-ar-dark-camera-settings.png',
    before: 'assets/v/p6-ar-dark-unbalanced-before.png', after: 'assets/v/p6-ar-dark-balanced-after.png',
    hit: 'assets/v/p9-ar-dark-overlay-hit.png', combo: 'assets/v/p9-ar-dark-combo-editor.png', hitTest: 'assets/v/p9-ar-dark-hit-test-dialog.png',
    mInst: 'assets/v/reel-ar-models-installed.png', devSet: 'assets/v/reel-ar-ai-settings.png', devPop: 'assets/v/reel-ar-device-popup.png',
    logo: 'assets/logo.png', nk: 'assets/nakhla/nakhla-logo-ondark.png',
  },
  hits: HITS, samples: () => 8,
  async init() { await loadSeq(); },
  draw(ctx, u, t, IMG) {
    IMGS = IMG; LOGO = IMG.logo; NK = IMG.nk;
    const i = SCENES.findIndex(([a, b]) => u < b), fn = SCENES[i < 0 ? SCENES.length - 1 : i][2];
    if (u > 7.3 && u < 8.7) {                           // the drop: an orange band wipes out of the studio intro
      const p = E.inOutCubic(prog(u, 7.3, 8.7)), bh = 760, c = lerp(H + bh / 2, -bh / 2, p);
      ctx.save(); ctx.beginPath(); ctx.rect(0, 0, W, Math.max(0, c - bh / 2)); ctx.clip(); s0(ctx, Math.min(u, 7.99)); ctx.restore();
      ctx.save(); ctx.beginPath(); ctx.rect(0, Math.min(H, c + bh / 2), W, H); ctx.clip(); s1(ctx, Math.max(u, 8)); ctx.restore();
      ctx.fillStyle = C.accent; ctx.fillRect(0, c - bh / 2, W, bh);
    } else fn(ctx, u);
  },
});
