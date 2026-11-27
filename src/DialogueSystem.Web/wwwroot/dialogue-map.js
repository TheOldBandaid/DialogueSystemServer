import { byId, run } from "./ui.js";
import { nodeWidth, nodeHeight, layoutMap, edgePoints, createSpatialIndex, edgeBounds, nodeBounds } from "./dialogue-map-layout.js";

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
  let refreshGeneration = 0;
  const rendered = new Map();
  let baseStatus = "";

  function syncNodes(left, top, width, height) {
    const visible = new Set(graph.nodeIndex.query({ left: left - 320, top: top - 180,
      right: left + width + 320, bottom: top + height + 180 }).map(node => node.line.id));
    const focusedId = Number(document.activeElement?.dataset?.lineId);
    if (graph.nodes.has(focusedId)) visible.add(focusedId);
    for (const [id, button] of rendered) {
      if (visible.has(id)) continue;
      button.remove(); rendered.delete(id);
      const node = graph.nodes.get(id);
      if (node) node.button = undefined;
    }
    const added = document.createDocumentFragment();
    for (const id of visible) {
      if (rendered.has(id)) continue;
      const node = graph.nodes.get(id);
      node.button = makeNode(node, graph.startLineId);
      rendered.set(id, node.button);
      added.append(node.button);
    }
    surface.append(added);
  }

  function redraw() {
    frame = undefined;
    if (!graph || !panel.open) return;

    const width = viewport.clientWidth;
    const height = viewport.clientHeight;
    if (width === 0 || height === 0) return;
    const left = viewport.scrollLeft;
    const top = viewport.scrollTop;
    syncNodes(left, top, width, height);
    if (!context) return;
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
    context.translate(-left, -top);

    const selected = graph.nodes.get(selectedId);
    const active = [];
    let visible = graph.edgeIndex.query({ left, top, right: left + width, bottom: top + height });
    const dense = visible.length > 1000;
    if (dense) {
      const local = visible.filter(edge => rendered.has(edge.source.line.id) && rendered.has(edge.target.line.id));
      const adjacent = local.filter(edge => edge.source === selected || edge.target === selected).slice(0, 1000);
      const rest = local.filter(edge => edge.source !== selected && edge.target !== selected).slice(0, 1000 - adjacent.length);
      visible = [...adjacent, ...rest];
    }
    status.textContent = dense ? "Связей много: показана часть стрелок. Все переходы доступны в списках выбранной реплики." : baseStatus;
    status.hidden = !status.textContent;
    for (const edge of visible) {
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
    function renderPage(limit) {
      const rows = edges.slice(0, limit).map(({ choice, source, target }) => {
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
      if (edges.length > limit) {
        const row = document.createElement("li");
        const more = document.createElement("button");
        more.type = "button";
        more.textContent = `Показать ещё ${incoming ? "входящие" : "исходящие"} переходы`;
        more.onclick = run(() => renderPage(limit + 20));
        row.append(more); host.append(row);
      }
    }
    renderPage(20);
  }

  function select(id) {
    graph?.nodes.get(selectedId)?.button?.setAttribute("aria-pressed", "false");
    selectedId = id;
    const node = graph?.nodes.get(id);
    selection.hidden = !node;
    center.disabled = !node;
    if (node) {
      node.button?.setAttribute("aria-pressed", "true");
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
    button.setAttribute("aria-pressed", String(line.id === selectedId));
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
    const request = ++refreshGeneration;
    let snapshot;
    try { snapshot = await loadSnapshot(); }
    catch (error) {
      if (request !== refreshGeneration) return;
      reset();
      status.textContent = error.message;
      status.hidden = false;
      throw error;
    }
    if (request !== refreshGeneration) return;
    if (graph && snapshot.revision != null && graph.revision === snapshot.revision && graph.id === snapshot.id) {
      select(selectedId);
      if (centerOnOpen) centerSelected();
      return;
    }
    graph = layoutMap(snapshot);
    graph.id = snapshot.id;
    graph.revision = snapshot.revision;
    graph.startLineId = snapshot.startLineId;
    for (const edge of graph.edges) {
      edge.points = edgePoints(edge);
      edge.bounds = edgeBounds(edge.points);
    }
    graph.nodeIndex = createSpatialIndex(graph.nodes.values(), nodeBounds);
    graph.edgeIndex = createSpatialIndex(graph.edges, edge => edge.bounds);

    const empty = graph.nodes.size === 0;
    baseStatus = empty ? "Добавьте реплики, чтобы увидеть карту." :
      snapshot.startLineId == null ? "Стартовая реплика не задана." : "";
    status.textContent = baseStatus;
    status.hidden = !status.textContent;
    content.hidden = empty;
    surface.style.width = `${graph.width}px`;
    surface.style.height = `${graph.height}px`;
    surface.replaceChildren();
    rendered.clear();
    select(selectedId);
    if (centerOnOpen) centerSelected();
  }

  function reset() {
    refreshGeneration++;
    graph = undefined;
    rendered.clear();
    surface.replaceChildren();
    content.hidden = true;
    selection.hidden = true;
    center.disabled = true;
  }

  panel.ontoggle = () => {
    if (panel.open) run(() => refresh(true))();
  };
  byId("show-map").addEventListener("click", () => { panel.open = true; });
  center.addEventListener("click", centerSelected);
  viewport.addEventListener("scroll", scheduleDraw, { passive: true });
  new ResizeObserver(scheduleDraw).observe(viewport);
  return { refresh, select, reset };
}
