// Blazma Animation: X (Twitter) cut. 16:9 1920x1080 and 1:1 1080x1080 from one source. NO music.
// A pure function of time, driven by assets/edit.json (built by tools/edit.mjs from the real screen recording's marks).
// Look: a person's screen recording, hand-edited. Real app window with a visible mouse pointer, simple cuts, creator-style
// subtitles (one style, bottom-centred), a few slow zoom-OUTS (footage is never scaled above 1.0x). No invented UI.
import * as M from './lib/motion.js';
const { W, H, E, prog, lerp, clamp, kf, font, fill, rrect, FORMAT } = M;
const SQ_ = FORMAT.square;
const C = { bg: '#1d1b19', panel: '#252320', raised: '#34312c', line: '#3b3833', lineStrong: '#4a463f', text: '#ece7df', text2: '#b4ada2', accent: '#c27a3e', onAccent: '#1d1b19' };
const DISP = 'Display', UI = 'UI';

// ------------------------------------------------------------------ data
const EDIT = await (await fetch('assets/edit.json')).json();
const REC = EDIT.rec;                                              // { fps: 30, frames: n }
const SEGS = EDIT.segments, CAPS = EDIT.captions;
const segAt = (t) => { for (const s of SEGS) if (t < s.t1) return s; return SEGS[SEGS.length - 1]; };

// ------------------------------------------------------------------ frames
const FR = { r: {}, n: {} };
async function loadSeq() {
  const jobs = [];
  const want = (k, i, dir, max) => { if (i < 1 || i > max || FR[k][i]) return; const im = new Image(); im.src = `assets/seq/${dir}/${String(i).padStart(5, '0')}.jpg`; jobs.push(new Promise((r) => { im.onload = r; im.onerror = r; })); FR[k][i] = im; };
  for (const s of SEGS) {
    if (s.id === 'intro') for (let i = 1; i <= 212; i++) want('n', i, 'n', 210);
    if (s.rec) { const a = Math.floor(s.rec[0] * REC.fps), b = Math.ceil(s.rec[1] * REC.fps) + 2; for (let i = a; i <= b + 1; i++) want('r', i + 1, 'r', REC.frames); }
  }
  await Promise.all(jobs);
}
function rf(sec) {                                                  // recording frame(s) at `sec`, neighbours blended (sub-frame smoothness)
  const f = Math.max(0, sec) * REC.fps, a = Math.floor(f);
  return [[FR.r[a + 1] || FR.r[a], 1], [FR.r[a + 2], f - a]];
}

// ------------------------------------------------------------------ type
function T(ctx, str, x, y, size, color, o = {}) {
  const { fam = DISP, w = 600, align = 'center', rtl = true, alpha = 1 } = o;
  ctx.save(); ctx.font = font(size, w, fam); ctx.fillStyle = color; ctx.textAlign = align; ctx.direction = rtl ? 'rtl' : 'ltr'; ctx.globalAlpha = alpha; ctx.fillText(str, x, y); ctx.restore();
}
const _m = new Map();
function tw(ctx, str, size, fam = DISP, w = 600, rtl = true) {
  const k = `${str}|${size}|${fam}|${w}|${rtl}`; if (_m.has(k)) return _m.get(k);
  ctx.save(); ctx.font = font(size, w, fam); ctx.direction = rtl ? 'rtl' : 'ltr'; const v = ctx.measureText(str).width; ctx.restore(); _m.set(k, v); return v;
}
function wrapLines(ctx, str, size, maxW, fam, w) {
  const out = []; let cur = [];
  for (const wd of str.split(' ')) { const t = [...cur, wd].join(' '); if (cur.length && tw(ctx, t, size, fam, w) > maxW) { out.push(cur.join(' ')); cur = [wd]; } else cur.push(wd); }
  out.push(cur.join(' ')); return out;
}
// creator-style subtitle: one style for the whole film, bottom-centred, quick fade, no kinetic motion
function subtitle(ctx, t) {
  const size = FORMAT.pick({ '16x9': 74, '1x1': 66 }), maxW = FORMAT.pick({ '16x9': 1560, '1x1': 830 }), baseY = FORMAT.pick({ '16x9': 960, '1x1': 975 });
  for (const [a, b, str, pos] of CAPS) {
    if (t < a || t >= b) continue;
    const al = Math.min(prog(t, a, a + 0.14), 1 - prog(t, b - 0.12, b));
    const lines = wrapLines(ctx, str, size, maxW, DISP, 600), lh = size * 1.32, n = lines.length;
    const widths = lines.map((l) => tw(ctx, l, size, DISP, 600)), bw = Math.max(...widths) + 64, bh = n * lh + 26, y0 = (pos === 'top' ? FORMAT.pick({ '16x9': 150, '1x1': 150 }) : baseY - (n - 1) * lh - size * 0.95 - 8) + (1 - al) * 8;
    ctx.save(); ctx.globalAlpha = al;
    rrect(ctx, (W - bw) / 2, y0, bw, bh, 22); ctx.fillStyle = 'rgba(17,15,13,.84)'; ctx.fill();
    lines.forEach((l, i) => T(ctx, l, W / 2, y0 + 13 + size * 0.95 + i * lh, size, '#f3eee6'));
    ctx.restore();
  }
}

// ------------------------------------------------------------------ footage placement (1.0x only)
function place(ctx, imgs, o = {}) {                                // imgs: [[img, alpha], ...]; 16:9 shows the whole window, 1:1 crops a 1080 window at native size
  const { fx = 960, s = 1 } = o;
  fill(ctx, C.bg); ctx.imageSmoothingQuality = 'high';
  ctx.save();
  if (s !== 1) { ctx.translate(W / 2, H / 2); ctx.scale(s, s); ctx.translate(-W / 2, -H / 2); }
  const sx = SQ_ ? clamp(fx - 540, 0, 840) : 0, sw = SQ_ ? 1080 : 1920;
  for (const [im, a] of imgs) if (im && a > 0.02) { ctx.globalAlpha = a; ctx.drawImage(im, sx, 0, sw, 1080, 0, 0, W, H); }
  ctx.restore(); ctx.globalAlpha = 1;
}
function card(ctx, img, x, y, w, h, src, o = {}) {                 // a real capture shown at its own pixel size (w,h = src size), with a hairline border
  const { alpha = 1, r = 12, lift = 0 } = o; if (alpha <= 0.01) return;
  ctx.save(); ctx.globalAlpha = alpha; ctx.shadowColor = 'rgba(0,0,0,.5)'; ctx.shadowBlur = 40; ctx.shadowOffsetY = 14;
  rrect(ctx, x, y + lift, w, h, r); ctx.fillStyle = C.panel; ctx.fill(); ctx.restore();
  ctx.save(); ctx.globalAlpha = alpha; rrect(ctx, x, y + lift, w, h, r); ctx.clip(); ctx.imageSmoothingQuality = 'high';
  const s = src || [0, 0, img.width, img.height]; ctx.drawImage(img, s[0], s[1], s[2], s[3], x, y + lift, w, h); ctx.restore();
  ctx.save(); ctx.globalAlpha = alpha; rrect(ctx, x + 1, y + 1 + lift, w - 2, h - 2, r); ctx.strokeStyle = C.lineStrong; ctx.lineWidth = 2; ctx.stroke(); ctx.restore();
}
const fade = (t, a, d = 0.22) => E.outCubic(prog(t, a, a + d));
const drift = (t, a, b, s0 = 1, s1 = 0.972) => lerp(s0, s1, E.inOutSine(prog(t, a, b)));   // slow zoom-OUT only

// ------------------------------------------------------------------ scenes
let IM = {};
function sIntro(ctx, s, t) {                                         // Studio Nakhla's own intro (first 3.5 s, before the partner logos), native 1920x1080
  const f = Math.min(Math.floor(t * 60) + 1, 210), im = FR.n[f] || FR.n[210]; fill(ctx, '#06140f'); if (!im) return;
  if (SQ_) ctx.drawImage(im, 420, 0, 1080, 1080, 0, 0, W, H); else ctx.drawImage(im, 0, 0, W, H);
}
function sRec(ctx, s, t) {
  const dt = t - s.t0, sec = s.rec[0] + dt * (s.speed || 1);
  const fx = s.fx ? kf(dt, s.fx, E.inOutCubic) : 960, sc = s.zo ? drift(dt, 0, s.t1 - s.t0, 1, s.zo) : 1;
  place(ctx, rf(sec), { fx, s: sc });
}
function sCam(ctx, s, t) {
  const dt = t - s.t0, sc = drift(dt, 0.2, s.t1 - s.t0);
  place(ctx, [[IM.cam, 1]], { fx: 1100, s: sc });
  const x0 = SQ_ ? 70 : 1330;
  card(ctx, IM.camMenu, x0, SQ_ ? 130 : 130, 270, 328, null, { alpha: fade(dt, 0.9), lift: (1 - fade(dt, 0.9)) * 10 });
  card(ctx, IM.camSet, x0 + (SQ_ ? 300 : -450), SQ_ ? 130 : 130, 420, 270, null, { alpha: fade(dt, 1.7), lift: (1 - fade(dt, 1.7)) * 10 });
}
function sBal(ctx, s, t) {                                           // before, a plain cut to after (what an editor would do)
  const dt = t - s.t0, after = dt >= s.cut; place(ctx, [[after ? IM.after : IM.before, 1]], { fx: 960, s: drift(after ? dt - s.cut : dt, 0, 2.2, 1, 0.985) });
}
function sHit(ctx, s, t) {
  const dt = t - s.t0; place(ctx, [[IM.hit, 1]], { fx: 960, s: drift(dt, 0, s.t1 - s.t0, 1, 0.98) });
  if (dt > s.cut) { ctx.fillStyle = `rgba(29,27,25,${0.62 * fade(dt, s.cut, 0.2)})`; ctx.fillRect(0, 0, W, H);
    const a = fade(dt, s.cut + 0.05, 0.25); card(ctx, IM.hitTest, (W - 600) / 2, (H - 571) / 2 - 40, 600, 571, null, { alpha: a, lift: (1 - a) * 12 }); }
}
function sAi(ctx, s, t) {
  const dt = t - s.t0; fill(ctx, C.bg);
  place(ctx, [[IM.cam, 1]], { fx: 960, s: 1 }); ctx.fillStyle = 'rgba(29,27,25,.86)'; ctx.fillRect(0, 0, W, H);
  if (dt < s.cut) { const a = fade(dt, 0.05, 0.25); card(ctx, IM.mInst, (W - 760) / 2, SQ_ ? 90 : 90 - (1 - a) * 0, 760, 720, null, { alpha: a }); }
  else {
    const a = fade(dt, s.cut, 0.25), x = (W - 680) / 2;
    card(ctx, IM.devSet, x, SQ_ ? 180 : 190, 680, 155, [0, 245, 680, 155], { alpha: a, lift: (1 - a) * 10 });
    const b = fade(dt, s.cut + 0.5, 0.25); card(ctx, IM.devPop, x + 60, (SQ_ ? 180 : 190) + 175, 537, 128, null, { alpha: b, lift: (1 - b) * 10 });
  }
}
function sEnd(ctx, s, t) {
  const dt = t - s.t0; fill(ctx, C.bg);
  const A = (a) => fade(dt, a, 0.45), R = FORMAT.pick({ '16x9': 1700, '1x1': 540 }), al = SQ_ ? 'center' : 'right';
  const L = SQ_ ? { ix: 540, iy: 120, is: 230, ty: 470, ay: 590, uy: 680, cy: 790, cw: 190 } : { ix: 460, iy: 210, is: 340, ty: 360, ay: 520, uy: 620, cy: 760, cw: 210 };
  const ia = A(0); ctx.save(); ctx.globalAlpha = ia; rrect(ctx, L.ix - L.is / 2, L.iy + (1 - ia) * 14, L.is, L.is, L.is * 0.21); ctx.clip(); ctx.drawImage(IM.logo, L.ix - L.is / 2, L.iy + (1 - ia) * 14, L.is, L.is); ctx.restore();
  const tx = SQ_ ? 540 : R, o = (a) => ({ alpha: A(a), align: al });
  T(ctx, 'Blazma Animation', tx, L.ty + (1 - A(0.35)) * 12, SQ_ ? 76 : 96, C.text, { ...o(0.35), rtl: false });
  T(ctx, 'حمّله من GitHub', tx, L.ay + (1 - A(0.8)) * 12, SQ_ ? 84 : 104, C.accent, o(0.8));
  T(ctx, 'github.com/mr-kateba/blazma-Animation', tx, L.uy + (1 - A(1.3)) * 10, SQ_ ? 34 : 42, C.text2, { ...o(1.3), fam: UI, w: 400, rtl: false });
  const ca = A(2.0), lh = L.cw * IM.nk.height / IM.nk.width, cx = SQ_ ? 540 - L.cw / 2 : R - L.cw;
  ctx.save(); ctx.globalAlpha = ca; ctx.drawImage(IM.nk, cx, L.cy + (1 - ca) * 10, L.cw, lh); ctx.restore();
  T(ctx, 'من استوديو نخلة', SQ_ ? 540 : R - L.cw / 2, L.cy + lh + 56, SQ_ ? 36 : 40, C.text2, { alpha: ca, fam: UI, w: 400 });
}
const SCENES = { intro: sIntro, rec: sRec, cam: sCam, bal: sBal, hit: sHit, ai: sAi, end: sEnd };

// ------------------------------------------------------------------ hits (seconds -> beats on the grid; sfx.mjs voices them)
const HITS = EDIT.hits.map(([sec, label, sfx, opts]) => [(sec - 0.00271) / 0.46874, label, sfx, opts || {}]);

M.film({
  fonts: [font(100, 600, DISP), font(40, 400, UI), font(40, 600, UI)],
  images: {
    cam: 'assets/v/p10-ar-dark-camera-viewport.png', camMenu: 'assets/v/p10-ar-dark-camera-view-menu.png', camSet: 'assets/v/p10-ar-dark-camera-settings.png',
    before: 'assets/v/p6-ar-dark-unbalanced-before.png', after: 'assets/v/p6-ar-dark-balanced-after.png',
    hit: 'assets/v/p9-ar-dark-overlay-hit.png', hitTest: 'assets/v/p9-ar-dark-hit-test-dialog.png',
    mInst: 'assets/v/reel-ar-models-installed.png', devSet: 'assets/v/reel-ar-ai-settings.png', devPop: 'assets/v/reel-ar-device-popup.png',
    logo: 'assets/logo-1024.png', nk: 'assets/nakhla/nakhla-logo-ondark.png',
  },
  hits: HITS, samples: () => 6,
  async init() { await loadSeq(); },
  draw(ctx, u, t, IMG) {
    IM = IMG; const s = segAt(t); (SCENES[s.id] || sRec)(ctx, s, t); if (s.id !== 'intro' && s.id !== 'end') subtitle(ctx, t); else if (s.id === 'end') subtitle(ctx, t);
  },
});
