export const nodeWidth = 218;
export const nodeHeight = 86;
const columnGap = 86;
const rowGap = 38;
const margin = 26;

export function layoutMap(snapshot) {
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

export function edgePoints({ source, target, offset }) {
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

export function nodeBounds(node) {
  return { left: node.x, top: node.y, right: node.x + nodeWidth, bottom: node.y + nodeHeight };
}

export function edgeBounds(points) {
  const [x0, y0, x1, y1, x2, y2, x3, y3] = points;
  return { left: Math.min(x0, x1, x2, x3) - 10, right: Math.max(x0, x1, x2, x3) + 10,
    top: Math.min(y0, y1, y2, y3) - 10, bottom: Math.max(y0, y1, y2, y3) + 10 };
}

function intersects(a, b) {
  return a.right >= b.left && a.left <= b.right && a.bottom >= b.top && a.top <= b.bottom;
}

export function createSpatialIndex(items, boundsFor) {
  function build(rows) {
    if (!rows.length) return null;
    const bounds = { left: Infinity, top: Infinity, right: -Infinity, bottom: -Infinity };
    for (const row of rows) {
      bounds.left = Math.min(bounds.left, row.bounds.left); bounds.top = Math.min(bounds.top, row.bounds.top);
      bounds.right = Math.max(bounds.right, row.bounds.right); bounds.bottom = Math.max(bounds.bottom, row.bounds.bottom);
    }
    if (rows.length <= 12) return { bounds, rows };
    const horizontal = bounds.right - bounds.left >= bounds.bottom - bounds.top;
    rows.sort((a, b) => horizontal
      ? a.bounds.left + a.bounds.right - b.bounds.left - b.bounds.right
      : a.bounds.top + a.bounds.bottom - b.bounds.top - b.bounds.bottom);
    const middle = Math.floor(rows.length / 2);
    return { bounds, left: build(rows.slice(0, middle)), right: build(rows.slice(middle)) };
  }
  const root = build([...items].map(item => ({ item, bounds: boundsFor(item) })));
  function query(bounds) {
    const matches = [];
    const pending = root ? [root] : [];
    while (pending.length) {
      const node = pending.pop();
      if (!intersects(node.bounds, bounds)) continue;
      if (node.rows) {
        for (const row of node.rows) if (intersects(row.bounds, bounds)) matches.push(row.item);
      } else pending.push(node.left, node.right);
    }
    return matches;
  }
  return { query };
}

