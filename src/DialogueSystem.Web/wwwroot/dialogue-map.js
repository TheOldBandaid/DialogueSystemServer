import { byId, run } from "./workspace.js";

const nodeWidth = 218;
const nodeHeight = 86;
const columnGap = 86;
const rowGap = 38;
const margin = 26;

function layoutMap(snapshot) {
  const nodes = new Map(snapshot.lines.map(line => [line.id, { line, incoming: [], outgoing: [] }]));
  const edges = [];
  const pairs = new Map();
  for (const choice of snapshot.choices) {
    const source = nodes.get(choice.sourceLineId);
    const target = nodes.get(choice.targetLineId);
    if (!source || !target) continue;

    const edge = { choice, source, target, offset: 0 };
    source.outgoing.push(edge);
    target.incoming.push(edge);
    edges.push(edge);

    const key = `${choice.sourceLineId}/${choice.targetLineId}`;
    const group = pairs.get(key) ?? [];
    group.push(edge);
    pairs.set(key, group);
  }
  for (const group of pairs.values()) {
    const { source, target } = group[0];
    const reciprocal = source !== target && pairs.has(`${target.line.id}/${source.line.id}`);
    const laneOffset = reciprocal ? (source.line.id < target.line.id ? -7 : 7) : 0;
    const spacing = Math.min(12, 34 / group.length);
    group.forEach((edge, index) => { edge.offset = laneOffset + (index - (group.length - 1) / 2) * spacing; });
  }
  if (nodes.size === 0) return { nodes, edges, width: 0, height: 0 };

  const root = nodes.get(snapshot.startLineId) ?? nodes.values().next().value;
  const depths = new Map([[root, 0]]);
  const queue = [root];
  const columns = [];
  for (let index = 0; index < queue.length; index++) {
    const node = queue[index];
    const depth = depths.get(node);
    const column = columns[depth] ??= [];
    column.push(node);
    for (const { target } of node.outgoing) {
      if (depths.has(target)) continue;
      depths.set(target, depth + 1);
      queue.push(target);
    }
  }

  function place(node, column, row) {
    node.x = margin + column * (nodeWidth + columnGap);
    node.y = margin + row * (nodeHeight + rowGap);
  }

  let rows = 0;
  columns.forEach((column, depth) => {
    rows = Math.max(rows, column.length);
    column.forEach((node, row) => place(node, depth, row));
  });

  const remaining = [...nodes.values()].filter(node => !depths.has(node));
  const remainingColumns = Math.min(remaining.length, Math.max(2, Math.min(4, columns.length)));
  remaining.forEach((node, index) => place(node, index % remainingColumns,
    rows + 1 + Math.floor(index / remainingColumns)));
  if (remainingColumns) rows += 1 + Math.ceil(remaining.length / remainingColumns);

  const width = margin + Math.max(columns.length, remainingColumns) * (nodeWidth + columnGap);
  const height = margin * 2 + rows * (nodeHeight + rowGap) - rowGap;
  return { nodes, edges, width, height };
}

function edgePoints({ source, target, offset }) {
  const right = source.x + nodeWidth;
  const sourceY = source.y + nodeHeight / 2 + offset;
  const targetY = target.y + nodeHeight / 2 + offset;

  if (source === target) {
    const bend = right + 35 + offset;
    return [right, sourceY - 15, bend, sourceY - 22, bend, sourceY + 22, right, sourceY + 15];
  }

  if (source.x === target.x) {
    const bend = right + 40 + offset;
    return [right, sourceY, bend, sourceY, bend, targetY, right, targetY];
  }

  const direction = target.x > source.x ? 1 : -1;
  const from = direction === 1 ? right : source.x;
  const to = direction === 1 ? target.x : target.x + nodeWidth;
  const bend = Math.min(100, Math.abs(to - from) * 0.55) * direction;
  return [from, sourceY, from + bend, sourceY, to - bend, targetY, to, targetY];
}

function drawEdge(context, edge, selected) {
  const [x0, y0, x1, y1, x2, y2, x3, y3] = edge.points;
  const conditional = Boolean(edge.choice.requiredState);
  const color = conditional ? "#865b23" : selected ? "#23674f" : "#667982";
  context.strokeStyle = color;
  context.fillStyle = color;
  context.lineWidth = selected ? 3 : 1.5;
  context.setLineDash(conditional ? [6, 4] : []);
  context.beginPath();
  context.moveTo(x0, y0);
  context.bezierCurveTo(x1, y1, x2, y2, x3, y3);
  context.stroke();

  const direction = Math.atan2(y3 - y2, x3 - x2);
  const size = selected ? 9 : 7;
  context.beginPath();
  context.moveTo(x3, y3);
  context.lineTo(x3 - Math.cos(direction - 0.48) * size, y3 - Math.sin(direction - 0.48) * size);
  context.lineTo(x3 - Math.cos(direction + 0.48) * size, y3 - Math.sin(direction + 0.48) * size);
  context.closePath();
  context.fill();
}

function visibleEdges(edges, left, top, width, height) {
  const right = left + width;
  const bottom = top + height;
  return edges.filter(({ bounds }) =>
    bounds.right >= left && bounds.left <= right && bounds.bottom >= top && bounds.top <= bottom);
}

export function createDialogueMap(loadSnapshot, chooseLine) {
  const panel = byId("map-panel");
  const content = byId("map-content");
  const status = byId("map-status");
  const viewport = byId("map-viewport");
  const canvas = byId("map-edges");
  const surface = byId("map-nodes");
  const selection = byId("map-selection");
  const center = byId("map-center");
  const context = canvas.getContext("2d");
  let graph;
  let selectedId;
  let frame;

  function redraw() {
    frame = undefined;
    if (!graph || !panel.open || !context) return;

    const width = viewport.clientWidth;
    const height = viewport.clientHeight;
    const ratio = window.devicePixelRatio || 1;
    const pixelsWide = Math.round(width * ratio);
    const pixelsHigh = Math.round(height * ratio);
    if (canvas.width !== pixelsWide || canvas.height !== pixelsHigh) {
      canvas.width = pixelsWide;
      canvas.height = pixelsHigh;
      canvas.style.width = `${width}px`;
      canvas.style.height = `${height}px`;
    }
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    context.clearRect(0, 0, width, height);
    const left = viewport.scrollLeft;
    const top = viewport.scrollTop;
    context.translate(-left, -top);

    const selected = graph.nodes.get(selectedId);
    const active = [];
    for (const edge of visibleEdges(graph.edges, left, top, width, height)) {
      if (edge.source === selected || edge.target === selected) active.push(edge);
      else drawEdge(context, edge, false);
    }
    for (const edge of active) drawEdge(context, edge, true);
  }

  function scheduleDraw() {
    if (frame === undefined) frame = requestAnimationFrame(redraw);
  }

  function centerSelected() {
    const node = graph?.nodes.get(selectedId);
    if (!node) return;

    viewport.scrollTo(
      node.x + (nodeWidth - viewport.clientWidth) / 2,
      node.y + (nodeHeight - viewport.clientHeight) / 2
    );
  }

  function renderTransitions(host, edges, incoming) {
    const rows = edges.map(({ choice, source, target }) => {
      const neighbor = (incoming ? source : target).line;
      const link = document.createElement("button");
      link.type = "button";
      link.textContent = incoming ? `№${neighbor.id} → ${choice.text}` : `${choice.text} → №${neighbor.id}`;
      link.onclick = run(async () => {
        await chooseLine(neighbor.id);
        centerSelected();
      });
      const item = document.createElement("li");
      item.append(link);

      const details = [];
      if (choice.requiredState) details.push(`Доступен при «${choice.requiredState}»`);
      if (choice.nextState) details.push(`Новое состояние «${choice.nextState}»`);
      if (details.length) {
        const note = document.createElement("small");
        note.className = "map-condition";
        note.textContent = details.join(" · ");
        item.append(note);
      }
      return item;
    });
    if (rows.length === 0) {
      const empty = document.createElement("li");
      empty.textContent = "Нет переходов.";
      rows.push(empty);
    }
    host.replaceChildren(...rows);
  }

  function select(id) {
    graph?.nodes.get(selectedId)?.button.setAttribute("aria-pressed", "false");
    selectedId = id;
    const node = graph?.nodes.get(id);
    selection.hidden = !node;
    center.disabled = !node;
    if (node) {
      node.button.setAttribute("aria-pressed", "true");
      byId("map-selected-title").textContent = `Реплика №${id} · ${node.line.speaker}`;
      renderTransitions(byId("map-incoming"), node.incoming, true);
      renderTransitions(byId("map-outgoing"), node.outgoing, false);
    }
    scheduleDraw();
  }

  function makeNode(node, startLineId) {
    const { line, x, y } = node;
    const button = document.createElement("button");
    button.type = "button";
    button.className = "map-node";
    button.dataset.lineId = line.id;
    button.setAttribute("aria-pressed", "false");
    button.style.cssText = `left:${x}px;top:${y}px;width:${nodeWidth}px;height:${nodeHeight}px`;
    button.title = line.text;
    button.onclick = run(() => chooseLine(line.id));

    const heading = document.createElement("span");
    heading.className = "map-node-heading";
    heading.textContent = `№${line.id} · ${line.speaker}`;
    const text = document.createElement("span");
    text.className = "map-node-text";
    text.textContent = line.text;
    const labels = [];
    if (line.id === startLineId) labels.push("Старт");
    if (line.isStoryBranch) labels.push("Развилка");
    if (line.isEnding) labels.push("Концовка");
    const marker = document.createElement("span");
    marker.className = "map-node-marker";
    marker.textContent = labels.join(" · ");
    button.append(heading, text, marker);
    return button;
  }

  async function refresh(centerOnOpen = false) {
    if (!panel.open) return;

    const snapshot = await loadSnapshot();
    graph = layoutMap(snapshot);
    for (const edge of graph.edges) {
      edge.points = edgePoints(edge);
      const [x0, y0, x1, y1, x2, y2, x3, y3] = edge.points;
      edge.bounds = {
        left: Math.min(x0, x1, x2, x3) - 10,
        right: Math.max(x0, x1, x2, x3) + 10,
        top: Math.min(y0, y1, y2, y3) - 10,
        bottom: Math.max(y0, y1, y2, y3) + 10
      };
    }

    const empty = graph.nodes.size === 0;
    status.textContent = empty ? "Добавьте реплики, чтобы увидеть карту." :
      snapshot.startLineId == null ? "Стартовая реплика не задана." : "";
    status.hidden = !status.textContent;
    content.hidden = empty;
    surface.style.width = `${graph.width}px`;
    surface.style.height = `${graph.height}px`;
    const buttons = [...graph.nodes.values()].map(node => {
      node.button = makeNode(node, snapshot.startLineId);
      return node.button;
    });
    surface.replaceChildren(...buttons);
    select(selectedId);
    if (centerOnOpen) centerSelected();
  }

  panel.ontoggle = () => {
    if (panel.open) run(() => refresh(true))();
  };
  byId("show-map").addEventListener("click", () => { panel.open = true; });
  center.addEventListener("click", centerSelected);
  viewport.addEventListener("scroll", scheduleDraw, { passive: true });
  new ResizeObserver(scheduleDraw).observe(viewport);
  return { refresh, select };
}
