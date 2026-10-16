// Blazma Animation — shared mockup helpers (static mockups only).
// - Language toggle (en / ar with RTL). Viewport and timeline stay LTR.
// - Theme toggle (dark / light).
// - drawScene(): draws a grid + humanoid mannequin into an <svg>.

(function () {
  const root = document.documentElement;
  const store = {
    get(k) { try { return localStorage.getItem(k); } catch (e) { return null; } },
    set(k, v) { try { localStorage.setItem(k, v); } catch (e) {} },
  };

  function applyLang(lang) {
    root.lang = lang;
    root.dir = lang === "ar" ? "rtl" : "ltr";
    store.set("blazma-mock-lang", lang);
    document.querySelectorAll("[data-set-lang]").forEach(b =>
      b.classList.toggle("on", b.dataset.setLang === lang));
  }
  function applyTheme(theme) {
    root.dataset.theme = theme;
    store.set("blazma-mock-theme", theme);
    document.querySelectorAll("[data-set-theme]").forEach(b =>
      b.classList.toggle("on", b.dataset.setTheme === theme));
    document.querySelectorAll("svg[data-scene]").forEach(redraw);
  }

  // URL params win (?lang=ar&theme=light) so screenshots are reproducible.
  const q = new URLSearchParams(location.search);
  document.addEventListener("DOMContentLoaded", () => {
    applyLang(q.get("lang") || store.get("blazma-mock-lang") || "en");
    applyTheme(q.get("theme") || store.get("blazma-mock-theme") || "dark");
    document.addEventListener("click", e => {
      const l = e.target.closest("[data-set-lang]");
      if (l) applyLang(l.dataset.setLang);
      const t = e.target.closest("[data-set-theme]");
      if (t) applyTheme(t.dataset.setTheme);
    });
    document.querySelectorAll("svg[data-scene]").forEach(redraw);
  });

  // ---- Scene drawing -------------------------------------------------------
  const css = n => getComputedStyle(root).getPropertyValue(n).trim();
  const NS = "http://www.w3.org/2000/svg";
  function el(tag, attrs, parent) {
    const e = document.createElementNS(NS, tag);
    for (const k in attrs) e.setAttribute(k, attrs[k]);
    parent.appendChild(e);
    return e;
  }

  // Poses: 2D joint positions in a 200x260 local box (y down), roughly front-3/4 view.
  const POSES = {
    idle: {
      head: [100, 22], neck: [100, 46], chest: [100, 70], pelvis: [100, 128],
      shL: [80, 52], elL: [72, 90], haL: [70, 126], shR: [120, 52], elR: [128, 90], haR: [131, 126],
      hipL: [90, 132], knL: [88, 188], ftL: [87, 244], hipR: [110, 132], knR: [113, 188], ftR: [114, 244],
    },
    action: {
      head: [112, 30], neck: [106, 52], chest: [100, 76], pelvis: [92, 132],
      shL: [84, 58], elL: [60, 76], haL: [44, 60], shR: [124, 56], elR: [150, 70], haR: [178, 58],
      hipL: [82, 136], knL: [60, 182], ftL: [44, 238], hipR: [102, 136], knR: [130, 180], ftR: [150, 238],
    },
  };
  const BONES = [
    ["neck", "head"], ["chest", "neck"], ["pelvis", "chest"],
    ["neck", "shL"], ["shL", "elL"], ["elL", "haL"], ["neck", "shR"], ["shR", "elR"], ["elR", "haR"],
    ["pelvis", "hipL"], ["hipL", "knL"], ["knL", "ftL"], ["pelvis", "hipR"], ["hipR", "knR"], ["knR", "ftR"],
  ];

  function redraw(svg) {
    while (svg.firstChild) svg.removeChild(svg.firstChild);
    const W = svg.clientWidth || 800, H = svg.clientHeight || 500;
    svg.setAttribute("viewBox", `0 0 ${W} ${H}`);
    const opt = svg.dataset;
    const cx = W / 2, horizon = H * 0.38, floorY = H * 0.86;

    // Perspective floor grid
    const g = el("g", { stroke: css("--grid"), "stroke-width": 1 }, svg);
    for (let i = -10; i <= 10; i++) {
      const x0 = cx + i * W * 0.012, x1 = cx + i * W * 0.11;
      el("line", { x1: x0, y1: horizon + 30, x2: x1, y2: H + 40, stroke: i === 0 ? css("--grid-axis") : css("--grid") }, g);
    }
    for (let j = 0; j < 12; j++) {
      const t = Math.pow(j / 11, 1.8), y = horizon + 30 + t * (H + 40 - horizon - 30);
      el("line", { x1: 0, y1: y, x2: W, y2: y }, g);
    }

    const scale = Math.min(H / 300, W / 260) * (Number(opt.scale) || 1);
    const ox = cx - 100 * scale + (Number(opt.offset) || 0) * W, oy = floorY - 244 * scale;
    const P = (pose, k) => [ox + pose[k][0] * scale, oy + pose[k][1] * scale];

    // Ghost of previous pose (onion skin)
    if (opt.onion !== undefined) {
      const go = el("g", { stroke: css("--text-3"), "stroke-width": 3 * scale, "stroke-linecap": "round", opacity: 0.35 }, svg);
      BONES.forEach(([a, b]) => { const p = P(POSES.idle, a), q = P(POSES.idle, b); el("line", { x1: p[0], y1: p[1], x2: q[0], y2: q[1] }, go); });
    }
    const pose = POSES[opt.pose || "idle"];

    // Motion trail on right hand
    if (opt.trail !== undefined) {
      const a = P(POSES.idle, "haR"), b = P(pose, "haR");
      el("path", { d: `M${a[0]},${a[1]} Q${(a[0] + b[0]) / 2 + 40 * scale},${a[1] - 10 * scale} ${b[0]},${b[1]}`,
        fill: "none", stroke: css("--accent"), "stroke-width": 1.5, "stroke-dasharray": "4 4" }, svg);
    }

    // Support polygon + centre of mass
    if (opt.com !== undefined) {
      const l = P(pose, "ftL"), r = P(pose, "ftR");
      el("ellipse", { cx: (l[0] + r[0]) / 2, cy: floorY, rx: Math.abs(r[0] - l[0]) / 2 + 14 * scale, ry: 7 * scale,
        fill: css("--accent-soft"), stroke: css("--accent"), "stroke-width": 1 }, svg);
      const c = P(pose, "pelvis");
      el("line", { x1: c[0], y1: c[1], x2: c[0], y2: floorY, stroke: css("--accent"), "stroke-dasharray": "2 3" }, svg);
      el("circle", { cx: c[0], cy: c[1], r: 5 * scale, fill: "none", stroke: css("--accent"), "stroke-width": 2 }, svg);
    }

    // Body (capsules) + bones
    const body = el("g", { stroke: css("--bone"), "stroke-linecap": "round" }, svg);
    BONES.forEach(([a, b]) => {
      const p = P(pose, a), q = P(pose, b);
      el("line", { x1: p[0], y1: p[1], x2: q[0], y2: q[1], "stroke-width": 9 * scale, opacity: 0.18 }, body);
      el("line", { x1: p[0], y1: p[1], x2: q[0], y2: q[1], "stroke-width": 2 }, body);
    });
    const h = P(pose, "head");
    el("circle", { cx: h[0], cy: h[1], r: 13 * scale, fill: css("--bone"), opacity: 0.18, stroke: css("--bone"), "stroke-width": 1.5 }, svg);
    Object.keys(pose).forEach(k => {
      if (k === "head") return;
      const p = P(pose, k), sel = k === (opt.sel || "elR");
      el("circle", { cx: p[0], cy: p[1], r: (sel ? 4.5 : 3) * scale,
        fill: sel ? css("--accent") : css("--viewport"), stroke: sel ? css("--accent") : css("--bone"), "stroke-width": 1.5 }, svg);
    });

    // IK targets on feet
    ["ftL", "ftR"].forEach(k => {
      const p = P(pose, k);
      el("rect", { x: p[0] - 7 * scale, y: p[1] - 3 * scale, width: 14 * scale, height: 6 * scale, rx: 2,
        fill: "none", stroke: css("--z"), "stroke-width": 1.5 }, svg);
    });

    // Rotate gizmo on selected joint
    if (opt.gizmo !== undefined) {
      const p = P(pose, opt.sel || "elR"), r = 26 * scale;
      el("circle", { cx: p[0], cy: p[1], r, fill: "none", stroke: css("--z"), "stroke-width": 1.5 }, svg);
      el("ellipse", { cx: p[0], cy: p[1], rx: r, ry: r * 0.35, fill: "none", stroke: css("--y"), "stroke-width": 1.5 }, svg);
      el("ellipse", { cx: p[0], cy: p[1], rx: r * 0.35, ry: r, fill: "none", stroke: css("--x"), "stroke-width": 1.5 }, svg);
    }

    // Axis triad bottom-left
    const ax = 34, ay = H - 30;
    el("line", { x1: ax, y1: ay, x2: ax + 22, y2: ay, stroke: css("--x"), "stroke-width": 2 }, svg);
    el("line", { x1: ax, y1: ay, x2: ax, y2: ay - 22, stroke: css("--y"), "stroke-width": 2 }, svg);
    el("line", { x1: ax, y1: ay, x2: ax - 12, y2: ay + 10, stroke: css("--z"), "stroke-width": 2 }, svg);
  }

  window.addEventListener("resize", () => document.querySelectorAll("svg[data-scene]").forEach(redraw));
  window.BlazmaMock = { redraw };
})();
