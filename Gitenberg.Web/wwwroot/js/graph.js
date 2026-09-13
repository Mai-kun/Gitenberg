// Interactive link graph: notes are nodes, [[WikiLink]]s are edges. Rendered
// on a canvas with a small force-directed layout — Barnes-Hut repulsion plus
// link springs — so a 1000-note vault still settles quickly. Supports pan,
// zoom, node dragging, hover highlighting of neighbors, an "orphans only"
// filter and opening a note on click. Colors come from the app CSS variables
// (re-read when the data-theme attribute or the OS scheme changes), so every
// theme works without per-theme drawing code.

// Layout tuning: alpha is the global "energy" — it decays each tick until the
// graph rests; dragging a node kicks it back up locally. setData pre-runs the
// whole settling phase synchronously, so the graph always opens already laid
// out instead of boiling in front of the user.
const ALPHA_START = 1;
const ALPHA_KICK = 0.3;
const ALPHA_DECAY = 0.012;
const ALPHA_MIN = 0.005;
const PRE_RUN_TICKS = 140;
const REPULSION = -2600;
const LINK_DISTANCE = 90;
const LINK_STRENGTH = 0.5;
const GRAVITY = 0.045;
const FRICTION = 0.82;
const THETA = 0.9; // Barnes-Hut opening angle
const MAX_TREE_DEPTH = 24; // guard against coincident points
const MAX_SPEED = 14;

const NODE_BASE_RADIUS = 5;
const NODE_DEGREE_SCALE = 2.1;
const NODE_MAX_RADIUS = 24;
const LABEL_ZOOM = 1.1; // show every label above this zoom
const ZOOM_MIN = 0.12;
const ZOOM_MAX = 5;

export function createGraphView({ canvas, onOpenNote, onStats }) {
  const ctx = canvas.getContext('2d');

  const sim = {
    nodes: [], // { path, name, degree, x, y, vx, vy, fixed }
    links: [], // { a, b } — indices into nodes
    activeNodes: [], // nodes taking part in the simulation and rendering
    alpha: 0,
    orphansOnly: false,
  };
  const view = { x: 0, y: 0, k: 1 }; // screen = world * k + (x, y)
  const pointers = new Map(); // pointerId -> { x, y }
  const drag = { node: null, moved: 0, startX: 0, startY: 0, pan: false, lastX: 0, lastY: 0 };
  const pinch = { active: false, startDist: 0, startK: 1, midX: 0, midY: 0 };
  let hovered = -1;
  let hoverNeighbors = new Set();
  let frameQueued = false;
  let colors = null;

  // -------------------------------------------------------------------------
  // Theme colors (CSS variables)
  // -------------------------------------------------------------------------

  function readColors() {
    const style = getComputedStyle(document.documentElement);
    const get = (name, fallback) => style.getPropertyValue(name).trim() || fallback;
    colors = {
      text: get('--app-text', '#222222'),
      hint: get('--app-hint', '#999999'),
      accent: get('--app-accent', '#2481cc'),
      link: get('--app-link', '#2481cc'),
      bg: get('--app-bg', '#ffffff'),
      sep: get('--app-section-sep', '#e0e0e0'),
    };
    invalidate();
  }

  // "auto" flips palettes through a prefers-color-scheme media query without
  // touching data-theme, hence the matchMedia listener next to the observer.
  new MutationObserver(readColors).observe(document.documentElement, {
    attributes: true,
    attributeFilter: ['data-theme'],
  });
  window.matchMedia?.('(prefers-color-scheme: dark)')?.addEventListener?.('change', readColors);
  readColors();

  // -------------------------------------------------------------------------
  // Layout simulation
  // -------------------------------------------------------------------------

  function startLayout(alpha) {
    sim.alpha = Math.max(sim.alpha, alpha);
    invalidate();
  }

  function tick() {
    const nodes = sim.activeNodes;
    const alpha = sim.alpha;
    if (alpha < ALPHA_MIN || !nodes.length) return;

    applyRepulsion(nodes, alpha);

    for (const link of sim.links) {
      const a = sim.nodes[link.a];
      const b = sim.nodes[link.b];
      if (!a || !b) continue;
      let dx = b.x - a.x;
      let dy = b.y - a.y;
      let dist = Math.hypot(dx, dy);
      if (dist < 1e-3) {
        // Coincident endpoints: nudge apart in a stable pseudo-random direction.
        dx = 0.01 * (link.a % 2 ? 1 : -1);
        dy = 0.01 * (link.b % 2 ? 1 : -1);
        dist = Math.hypot(dx, dy);
      }
      const force = (dist - LINK_DISTANCE) * LINK_STRENGTH * alpha / dist;
      if (!a.fixed) {
        a.vx += dx * force * 0.5;
        a.vy += dy * force * 0.5;
      }
      if (!b.fixed) {
        b.vx -= dx * force * 0.5;
        b.vy -= dy * force * 0.5;
      }
    }

    // Gravity towards the world origin keeps orphans (no links) on screen.
    for (const node of nodes) {
      if (node.fixed) continue;
      node.vx += -node.x * GRAVITY * alpha;
      node.vy += -node.y * GRAVITY * alpha;
    }

    for (const node of nodes) {
      if (node.fixed) continue;
      node.vx *= FRICTION;
      node.vy *= FRICTION;
      const speed = Math.hypot(node.vx, node.vy);
      if (speed > MAX_SPEED) {
        node.vx *= MAX_SPEED / speed;
        node.vy *= MAX_SPEED / speed;
      }
      node.x += node.vx;
      node.y += node.vy;
    }

    sim.alpha = Math.max(0, alpha - ALPHA_DECAY);
  }

  // Barnes-Hut quadtree: O(n log n) pairwise repulsion.
  function applyRepulsion(nodes, alpha) {
    const tree = buildTree(nodes);
    if (!tree) return;
    for (const node of nodes) {
      if (node.fixed) continue;
      repel(node, tree, alpha);
    }
  }

  function buildTree(nodes) {
    if (!nodes.length) return null;
    let minX = Infinity;
    let minY = Infinity;
    let maxX = -Infinity;
    let maxY = -Infinity;
    for (const node of nodes) {
      if (node.x < minX) minX = node.x;
      if (node.y < minY) minY = node.y;
      if (node.x > maxX) maxX = node.x;
      if (node.y > maxY) maxY = node.y;
    }
    const half = Math.max(maxX - minX, maxY - minY) / 2 + 1;
    const root = {
      x: (minX + maxX) / 2,
      y: (minY + maxY) / 2,
      half,
      points: [],
      children: null,
      mass: 0,
      cx: 0,
      cy: 0,
    };

    for (const node of nodes) {
      let branch = root;
      let depth = 0;
      while (true) {
        if (branch.children) {
          branch = childFor(branch, node);
          depth += 1;
          continue;
        }
        if (branch.points.length === 0) {
          branch.points.push(node);
          break;
        }
        if (branch.points.length === 1 && depth < MAX_TREE_DEPTH) {
          // Occupied leaf: subdivide, push the resident point one level down.
          const existing = branch.points.pop();
          branch.children = [null, null, null, null];
          childFor(branch, existing).points.push(existing);
          branch = childFor(branch, node);
          depth += 1;
          continue;
        }
        branch.points.push(node); // depth cap: bucket leaf
        break;
      }
    }

    aggregate(root);
    return root;
  }

  function childFor(branch, node) {
    const quadrant = (node.y >= branch.y ? 2 : 0) | (node.x >= branch.x ? 1 : 0);
    let child = branch.children[quadrant];
    if (!child) {
      child = {
        x: branch.x + (quadrant & 1 ? branch.half / 2 : -branch.half / 2),
        y: branch.y + (quadrant & 2 ? branch.half / 2 : -branch.half / 2),
        half: branch.half / 2,
        points: [],
        children: null,
        mass: 0,
        cx: 0,
        cy: 0,
      };
      branch.children[quadrant] = child;
    }
    return child;
  }

  function aggregate(branch) {
    if (branch.children) {
      let mass = 0;
      let sx = 0;
      let sy = 0;
      for (const child of branch.children) {
        if (!child) continue;
        aggregate(child);
        mass += child.mass;
        sx += child.cx * child.mass;
        sy += child.cy * child.mass;
      }
      branch.mass = mass;
      branch.cx = mass ? sx / mass : branch.x;
      branch.cy = mass ? sy / mass : branch.y;
    } else {
      branch.mass = branch.points.length;
      let sx = 0;
      let sy = 0;
      for (const point of branch.points) {
        sx += point.x;
        sy += point.y;
      }
      branch.cx = branch.mass ? sx / branch.mass : branch.x;
      branch.cy = branch.mass ? sy / branch.mass : branch.y;
    }
  }

  function repel(node, tree, alpha) {
    const stack = [tree];
    while (stack.length) {
      const branch = stack.pop();
      if (!branch || branch.mass === 0) continue;
      const dx = branch.cx - node.x;
      const dy = branch.cy - node.y;
      const dist2 = dx * dx + dy * dy;
      const dist = Math.sqrt(dist2);
      if (branch.children && branch.half * 2 > THETA * dist) {
        for (const child of branch.children) {
          if (child) stack.push(child);
        }
        continue;
      }
      if (branch.points.length === 1 && branch.points[0] === node) continue;
      const softened = dist + 1;
      const force = (REPULSION * alpha * branch.mass) / (softened * softened * softened);
      node.vx += dx * force;
      node.vy += dy * force;
    }
  }

  function refreshActiveNodes() {
    sim.activeNodes = sim.orphansOnly
      ? sim.nodes.filter((node) => node.degree === 0)
      : sim.nodes;
  }

  // -------------------------------------------------------------------------
  // Data & view fitting
  // -------------------------------------------------------------------------

  function setData(data) {
    const rawNodes = Array.isArray(data?.nodes) ? data.nodes : [];
    const rawLinks = Array.isArray(data?.links) ? data.links : [];

    const indexByPath = new Map();
    sim.nodes = rawNodes.map((node, i) => {
      indexByPath.set(node.path, i);
      // Golden-angle spiral: a deterministic, well-spread starting layout.
      const radius = 30 * Math.sqrt(i + 1);
      const angle = i * 2.39996;
      return {
        idx: i,
        path: node.path,
        name: node.name || node.path,
        degree: node.degree || 0,
        x: radius * Math.cos(angle),
        y: radius * Math.sin(angle),
        vx: 0,
        vy: 0,
        fixed: false,
      };
    });

    sim.links = [];
    for (const link of rawLinks) {
      const a = indexByPath.get(link.source);
      const b = indexByPath.get(link.target);
      if (a !== undefined && b !== undefined && a !== b) {
        sim.links.push({ a, b });
      }
    }

    hovered = -1;
    hoverNeighbors = new Set();
    sim.alpha = ALPHA_START;
    refreshActiveNodes();

    for (let i = 0; i < PRE_RUN_TICKS; i += 1) {
      tick();
    }
    if (typeof onStats === 'function') {
      const orphans = sim.nodes.filter((node) => node.degree === 0).length;
      onStats({ total: sim.nodes.length, links: sim.links.length, orphans });
    }
    fitView();
    invalidate();
  }

  function fitView() {
    const nodes = sim.activeNodes;
    if (!nodes.length) {
      view.x = canvas.clientWidth / 2;
      view.y = canvas.clientHeight / 2;
      view.k = 1;
      return;
    }
    let minX = Infinity;
    let minY = Infinity;
    let maxX = -Infinity;
    let maxY = -Infinity;
    for (const node of nodes) {
      if (node.x < minX) minX = node.x;
      if (node.y < minY) minY = node.y;
      if (node.x > maxX) maxX = node.x;
      if (node.y > maxY) maxY = node.y;
    }
    const width = Math.max(maxX - minX, 1) + NODE_BASE_RADIUS * 8;
    const height = Math.max(maxY - minY, 1) + NODE_BASE_RADIUS * 8;
    const k = Math.min(canvas.clientWidth / width, canvas.clientHeight / height, 1.6);
    view.k = Math.max(k, ZOOM_MIN);
    view.x = canvas.clientWidth / 2 - ((minX + maxX) / 2) * view.k;
    view.y = canvas.clientHeight / 2 - ((minY + maxY) / 2) * view.k;
  }

  function setOrphansOnly(only) {
    if (sim.orphansOnly === only) return;
    sim.orphansOnly = only;
    hovered = -1;
    hoverNeighbors = new Set();
    refreshActiveNodes();
    fitView();
    // Orphans have no springs, so re-seed their layout from scratch.
    startLayout(ALPHA_START);
  }

  function activate() {
    readColors();
    invalidate();
  }

  // -------------------------------------------------------------------------
  // Rendering
  // -------------------------------------------------------------------------

  function resizeCanvas() {
    const dpr = window.devicePixelRatio || 1;
    const width = canvas.clientWidth;
    const height = canvas.clientHeight;
    if (canvas.width !== Math.round(width * dpr) || canvas.height !== Math.round(height * dpr)) {
      canvas.width = Math.round(width * dpr);
      canvas.height = Math.round(height * dpr);
    }
    return dpr;
  }

  function invalidate() {
    if (frameQueued) return;
    frameQueued = true;
    requestAnimationFrame(frame);
  }

  function frame() {
    frameQueued = false;
    // The canvas lives in a hidden view between visits — skip drawing there,
    // the next activate() re-renders once it is visible again.
    if (canvas.offsetParent === null) return;
    if (sim.alpha >= ALPHA_MIN) {
      tick();
      invalidate(); // keep animating while the layout has energy
    }
    render();
  }

  function render() {
    const dpr = resizeCanvas();
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, canvas.clientWidth, canvas.clientHeight);
    if (!colors) return;

    const orphanMode = sim.orphansOnly;
    const highlight = hovered >= 0;

    // Orphans have no links by definition — skip the edge pass entirely.
    if (!orphanMode) {
      ctx.lineWidth = 1;
      ctx.strokeStyle = colors.link;
      for (const link of sim.links) {
        const a = sim.nodes[link.a];
        const b = sim.nodes[link.b];
        const related = !highlight || link.a === hovered || link.b === hovered
          || hoverNeighbors.has(link.a) || hoverNeighbors.has(link.b);
        if (highlight && !related) continue;
        ctx.globalAlpha = highlight ? 0.8 : 0.3;
        ctx.beginPath();
        ctx.moveTo(a.x * view.k + view.x, a.y * view.k + view.y);
        ctx.lineTo(b.x * view.k + view.x, b.y * view.k + view.y);
        ctx.stroke();
      }
    }

    // Nodes: connected notes filled with the accent color, orphans muted.
    for (const node of sim.nodes) {
      if (orphanMode && node.degree !== 0) continue;
      const index = node.idx;
      const sx = node.x * view.k + view.x;
      const sy = node.y * view.k + view.y;
      const radius = nodeRadius(node) * view.k;
      if (sx < -radius || sy < -radius || sx > canvas.clientWidth + radius || sy > canvas.clientHeight + radius) {
        continue;
      }
      const related = !highlight || index === hovered || hoverNeighbors.has(index);
      ctx.globalAlpha = related ? 1 : 0.22;
      ctx.fillStyle = node.degree === 0 ? colors.hint : colors.accent;
      ctx.beginPath();
      ctx.arc(sx, sy, radius, 0, Math.PI * 2);
      ctx.fill();
      if (index === hovered) {
        ctx.strokeStyle = colors.text;
        ctx.lineWidth = 1.5;
        ctx.beginPath();
        ctx.arc(sx, sy, radius + 3, 0, Math.PI * 2);
        ctx.stroke();
        ctx.lineWidth = 1;
      }
    }

    // Labels: hovered node + neighbors always, everything else at high zoom.
    ctx.font = '11px -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Arial, sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    ctx.lineWidth = 3;
    ctx.strokeStyle = colors.bg;
    for (const node of sim.nodes) {
      if (orphanMode && node.degree !== 0) continue;
      const index = node.idx;
      if (!(index === hovered || hoverNeighbors.has(index) || view.k >= LABEL_ZOOM)) continue;
      const related = !highlight || index === hovered || hoverNeighbors.has(index);
      const sx = node.x * view.k + view.x;
      const sy = node.y * view.k + view.y;
      const radius = nodeRadius(node) * view.k;
      if (sy < -20 || sy > canvas.clientHeight + 20 || sx < -80 || sx > canvas.clientWidth + 80) continue;
      ctx.globalAlpha = related ? 0.95 : 0.25;
      ctx.strokeText(node.name, sx, sy + radius + 3);
      ctx.fillStyle = colors.text;
      ctx.fillText(node.name, sx, sy + radius + 3);
    }

    ctx.globalAlpha = 1;
  }

  function nodeRadius(node) {
    if (node.degree === 0) return NODE_BASE_RADIUS * 0.8;
    return Math.min(NODE_BASE_RADIUS + Math.sqrt(node.degree) * NODE_DEGREE_SCALE, NODE_MAX_RADIUS);
  }

  // -------------------------------------------------------------------------
  // Interaction: pan / zoom / drag / hover / click-to-open
  // -------------------------------------------------------------------------

  function toWorld(px, py) {
    return { x: (px - view.x) / view.k, y: (py - view.y) / view.k };
  }

  function hitTest(px, py) {
    let best = -1;
    let bestDist = Infinity;
    for (const node of sim.nodes) {
      if (sim.orphansOnly && node.degree !== 0) continue;
      const sx = node.x * view.k + view.x;
      const sy = node.y * view.k + view.y;
      const dist = Math.hypot(px - sx, py - sy);
      const reach = nodeRadius(node) * view.k + 7;
      if (dist < reach && dist < bestDist) {
        best = node.idx;
        bestDist = dist;
      }
    }
    return best;
  }

  function setHovered(index) {
    if (index === hovered) return;
    hovered = index;
    hoverNeighbors = new Set();
    if (index >= 0) {
      for (const link of sim.links) {
        if (link.a === index) hoverNeighbors.add(link.b);
        if (link.b === index) hoverNeighbors.add(link.a);
      }
    }
    canvas.classList.toggle('node-hover', index >= 0);
    invalidate();
  }

  function localPoint(event) {
    const rect = canvas.getBoundingClientRect();
    return { x: event.clientX - rect.left, y: event.clientY - rect.top };
  }

  canvas.addEventListener('pointerdown', (event) => {
    canvas.setPointerCapture(event.pointerId);
    const point = localPoint(event);
    pointers.set(event.pointerId, point);

    if (pointers.size === 2) {
      // Second finger: switch to pinch zoom.
      if (drag.node) {
        drag.node.fixed = false;
        drag.node = null;
      }
      drag.pan = false;
      const [p1, p2] = [...pointers.values()];
      pinch.active = true;
      pinch.startDist = Math.hypot(p2.x - p1.x, p2.y - p1.y) || 1;
      pinch.startK = view.k;
      pinch.midX = (p1.x + p2.x) / 2;
      pinch.midY = (p1.y + p2.y) / 2;
      return;
    }

    const hit = hitTest(point.x, point.y);
    drag.moved = 0;
    drag.startX = point.x;
    drag.startY = point.y;
    drag.lastX = point.x;
    drag.lastY = point.y;
    if (hit >= 0) {
      drag.node = sim.nodes[hit];
      drag.node.fixed = true;
      drag.pan = false;
      setHovered(hit);
    } else {
      drag.pan = true;
      canvas.classList.add('is-panning');
    }
    invalidate();
  });

  canvas.addEventListener('pointermove', (event) => {
    const point = localPoint(event);
    if (pointers.has(event.pointerId)) {
      pointers.set(event.pointerId, point);
    }

    if (pinch.active && pointers.size >= 2) {
      const [p1, p2] = [...pointers.values()];
      const dist = Math.hypot(p2.x - p1.x, p2.y - p1.y) || 1;
      const midX = (p1.x + p2.x) / 2;
      const midY = (p1.y + p2.y) / 2;
      const k = Math.min(Math.max(pinch.startK * (dist / pinch.startDist), ZOOM_MIN), ZOOM_MAX);
      // Keep the pinch midpoint anchored while zooming, follow its movement.
      view.x = midX - ((pinch.midX - view.x) / view.k) * k;
      view.y = midY - ((pinch.midY - view.y) / view.k) * k;
      view.k = k;
      pinch.midX = midX;
      pinch.midY = midY;
      invalidate();
      return;
    }

    if (drag.node) {
      const world = toWorld(point.x, point.y);
      drag.node.x = world.x;
      drag.node.y = world.y;
      drag.node.vx = 0;
      drag.node.vy = 0;
      drag.moved = Math.max(drag.moved, Math.hypot(point.x - drag.startX, point.y - drag.startY));
      startLayout(ALPHA_KICK);
      return;
    }

    if (drag.pan) {
      view.x += point.x - drag.lastX;
      view.y += point.y - drag.lastY;
      drag.moved = Math.max(drag.moved, Math.hypot(point.x - drag.startX, point.y - drag.startY));
      drag.lastX = point.x;
      drag.lastY = point.y;
      invalidate();
      return;
    }

    if (pointers.size === 0) {
      setHovered(hitTest(point.x, point.y));
    }
  });

  function releasePointer(event) {
    pointers.delete(event.pointerId);
    canvas.classList.remove('is-panning');

    if (pinch.active && pointers.size < 2) {
      pinch.active = false;
    }

    if (drag.node) {
      const wasClick = drag.moved < 5;
      drag.node.fixed = false;
      if (wasClick && typeof onOpenNote === 'function') {
        onOpenNote(drag.node.path);
      } else {
        startLayout(ALPHA_KICK);
      }
      drag.node = null;
      invalidate();
      return;
    }

    drag.pan = false;
  }

  canvas.addEventListener('pointerup', releasePointer);
  canvas.addEventListener('pointercancel', releasePointer);
  canvas.addEventListener('pointerleave', () => setHovered(-1));

  canvas.addEventListener('wheel', (event) => {
    event.preventDefault();
    const point = localPoint(event);
    const world = toWorld(point.x, point.y);
    const factor = Math.exp(-event.deltaY * 0.0015);
    view.k = Math.min(Math.max(view.k * factor, ZOOM_MIN), ZOOM_MAX);
    view.x = point.x - world.x * view.k;
    view.y = point.y - world.y * view.k;
    invalidate();
  }, { passive: false });

  // Keep the fit fresh when the wrapper is resized (rotation, panel changes).
  const observer = new ResizeObserver(() => {
    if (canvas.offsetParent === null) return;
    fitView();
    invalidate();
  });
  observer.observe(canvas);

  return { setData, activate, setOrphansOnly };
}
