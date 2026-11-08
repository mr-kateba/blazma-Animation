// Studio intro module (beats 0-6): «استوديو نخلة». PLACEHOLDER: a plain dark card with the studio name in the display face
// and a dashed frame marked «مكان الشعار». Replace the block marked LOGO SLOT with ctx.drawImage(logoImage, ...) when the
// real logo is supplied (same slot, same timing); nothing else in the film depends on it.
import * as M from './lib/motion.js';
const { W, H, E, prog, springU, SPRING, font, fill, rrect } = M;
const C = { bg: '#1d1b19', text: '#ece7df', text2: '#b4ada2', accent: '#c27a3e', line: '#4a463f' };

export function studioIntro(ctx, u, logo = null) {
  fill(ctx, C.bg);
  const k = springU(u, 0, SPRING.bouncy), cx = W / 2, cy = 800;
  // ---- LOGO SLOT (placeholder) ----
  ctx.save(); ctx.translate(cx, cy); ctx.scale(0.9 + 0.1 * k, 0.9 + 0.1 * k);
  if (logo) ctx.drawImage(logo, -240, -240, 480, 480);
  else {
    rrect(ctx, -240, -240, 480, 480, 56); ctx.setLineDash([22, 16]); ctx.lineWidth = 4; ctx.strokeStyle = C.line; ctx.stroke(); ctx.setLineDash([]);
    ctx.font = font(40, 400, 'UI'); ctx.fillStyle = C.text2; ctx.textAlign = 'center'; ctx.direction = 'rtl'; ctx.fillText('مكان الشعار', 0, 12);
  }
  ctx.restore();
  // ---- name (display face), inside the TikTok safe area ----
  const p = springU(u, 0.4, SPRING.bouncy);
  if (p > 0.001) { ctx.save(); ctx.beginPath(); ctx.rect(0, 1000, W, 200); ctx.clip(); ctx.font = font(120, 600, 'Display'); ctx.fillStyle = C.text; ctx.textAlign = 'center'; ctx.direction = 'rtl'; ctx.fillText('استوديو نخلة', cx, 1130 + (1 - p) * 130); ctx.restore(); }
  const b = E.outExpo(prog(u, 1.6, 2.4)); rrect(ctx, cx - 110 * b, 1170, 220 * b, 8, 4); ctx.fillStyle = C.accent; ctx.fill();
}
