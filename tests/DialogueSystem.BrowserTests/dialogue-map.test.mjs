import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import { layoutMap, edgePoints, edgeBounds, nodeBounds, createSpatialIndex } from "../../src/DialogueSystem.Web/wwwroot/dialogue-map-layout.js";

test("map geometry keeps cycles, parallel edges and disconnected lines", () => {
  const graph = layoutMap({ startLineId: 1, lines: [1, 2, 3].map(id => ({ id })), choices: [
    { sourceLineId: 1, targetLineId: 2 }, { sourceLineId: 1, targetLineId: 2 },
    { sourceLineId: 2, targetLineId: 1 }, { sourceLineId: 1, targetLineId: 1 }
  ] });
  assert.equal(graph.nodes.size, 3);
  assert.equal(graph.edges.length, 4);
  assert.notEqual(graph.edges[0].offset, graph.edges[1].offset);
  assert.ok(graph.nodes.get(3).y > graph.nodes.get(1).y);
  for (const edge of graph.edges) assert.ok(edgePoints(edge).every(Number.isFinite));
  const index = createSpatialIndex(graph.edges, edge => edgeBounds(edgePoints(edge)));
  assert.equal(index.query({ left: -100, top: -100, right: graph.width + 100, bottom: graph.height + 100 }).length, 4);
  assert.deepEqual(createSpatialIndex([], nodeBounds).query({ left: 0, top: 0, right: 100, bottom: 100 }), []);
});

test("spatial index matches visible nodes at both ends of a 5000-line graph", () => {
  const graph = layoutMap({ startLineId: 1,
    lines: Array.from({ length: 5000 }, (_, index) => ({ id: index + 1 })),
    choices: Array.from({ length: 4999 }, (_, index) => ({ sourceLineId: index + 1, targetLineId: index + 2 })) });
  const index = createSpatialIndex(graph.nodes.values(), nodeBounds);
  for (const id of [1, 2500, 5000]) {
    const bounds = nodeBounds(graph.nodes.get(id));
    assert.deepEqual(index.query(bounds).map(node => node.line.id), [id]);
  }
});

test("large maps render a bounded viewport and can select a node outside the DOM", async () => {
  const dom = new JSDOM(`<form id="login"></form><div id="workspace"></div>
    <details id="account-admin"></details><form id="new-account"></form><form id="change-password"></form>
    <details id="my-account"></details><ul id="accounts-list"></ul><button id="refresh-accounts"></button>
    <p id="status"></p><button id="logout"></button>
    <details id="map-panel" open><div id="map-content"><p id="map-status"></p>
      <div id="map-viewport"><canvas id="map-edges"></canvas><div id="map-nodes"></div></div>
      <div id="map-selection"><p id="map-selected-title"></p><ul id="map-incoming"></ul><ul id="map-outgoing"></ul></div>
    </div></details><button id="map-center"></button><a id="show-map"></a>`, { url: "https://example.test" });
  globalThis.document = dom.window.document;
  globalThis.window = dom.window;
  const frames = [];
  globalThis.requestAnimationFrame = callback => { frames.push(callback); return frames.length; };
  globalThis.ResizeObserver = class { observe() {} };
  const viewport = document.getElementById("map-viewport");
  Object.defineProperties(viewport, { clientWidth: { value: 1000 }, clientHeight: { value: 500 } });
  viewport.scrollTo = (left, top) => {
    viewport.scrollLeft = Math.max(0, left); viewport.scrollTop = Math.max(0, top);
    viewport.dispatchEvent(new dom.window.Event("scroll"));
  };
  let strokes = 0;
  const context = Object.fromEntries(["setTransform", "clearRect", "translate", "setLineDash", "beginPath", "moveTo", "bezierCurveTo", "lineTo", "closePath", "fill"].map(name => [name, () => {}]));
  context.stroke = () => { strokes++; };
  document.getElementById("map-edges").getContext = () => context;
  const flush = () => { while (frames.length) frames.shift()(); };
  const lines = Array.from({ length: 5000 }, (_, index) => ({ id: index + 1, speaker: "Говорящий", text: `Реплика ${index + 1}` }));
  let snapshot = { id: 1, revision: 1, startLineId: 1, lines,
    choices: Array.from({ length: 4999 }, (_, index) => ({ sourceLineId: index + 1, targetLineId: index + 2, text: "Дальше" })) };
  try {
    const { createDialogueMap } = await import("../../src/DialogueSystem.Web/wwwroot/dialogue-map.js");
    const map = createDialogueMap(async () => snapshot, async id => map.select(id));
    await map.refresh(); flush();
    assert.ok(document.querySelectorAll(".map-node").length > 0);
    assert.ok(document.querySelectorAll(".map-node").length < 80);
    assert.equal(document.querySelector('[data-line-id="5000"]'), null);
    map.select(5000);
    document.getElementById("map-center").click(); flush();
    assert.equal(document.querySelector('[data-line-id="5000"]').getAttribute("aria-pressed"), "true");
    assert.ok(document.querySelectorAll(".map-node").length < 80);
    assert.ok(document.getElementById("map-edges").width <= 1000 * window.devicePixelRatio);
    snapshot = { ...snapshot, revision: 2, choices: Array.from({ length: 19996 }, (_, index) =>
      ({ sourceLineId: 1, targetLineId: 2 + index % 4999, text: "Связь" })) };
    viewport.scrollLeft = 0; viewport.scrollTop = 0;
    await map.refresh(); strokes = 0; flush();
    assert.ok(strokes <= 1000);
    assert.match(document.getElementById("map-status").textContent, /часть стрелок/);
    map.reset(); flush();
    assert.equal(document.querySelectorAll(".map-node").length, 0);
  } finally { dom.window.close(); }
});
