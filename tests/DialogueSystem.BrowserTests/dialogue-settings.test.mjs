import { test } from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { JSDOM } from "jsdom";

test("metadata controls send complete settings through one route and preserve rejected drafts", async () => {
  const layout = await readFile(new URL("../../src/DialogueSystem.Web/Views/Shared/_Layout.cshtml", import.meta.url), "utf8");
  const editor = await readFile(new URL("../../src/DialogueSystem.Web/Views/Pages/Editor.cshtml", import.meta.url), "utf8");
  const dom = new JSDOM(layout.replace("@RenderBody()", editor.replace('data-dialogue-id="@Model"', 'data-dialogue-id="1"')),
    { url: "https://example.test/dialogues/1" });
  for (const name of ["document", "window", "location", "history", "FormData", "Option"])
    globalThis[name] = name === "window" ? dom.window : dom.window[name];
  globalThis.ResizeObserver = class { observe() {} };
  globalThis.requestAnimationFrame = () => 1;
  dom.window.HTMLCanvasElement.prototype.getContext = () => null;
  dom.window.confirm = () => true;
  const oldFetch = globalThis.fetch;
  const line = { id: 1, speakerId: 1, speaker: "Персонаж", text: "Конец", isEnding: true, version: "A".repeat(64) };
  let version = 0;
  const current = { id: 1, title: "Исходное название", startLineId: 1, minimumPlayerLevel: 2,
    version: String(version).padStart(64, "0"), deleteVersion: "B".repeat(64) };
  const writes = [];
  globalThis.fetch = async (path, input = {}) => {
    if (path === "/api/accounts/me") return Response.json({ id: 1, role: "Editor" });
    if (path === "/api/accounts/csrf") return Response.json({ token: "csrf" });
    if (path === "/api/dialogues/1" && input.method === "PUT") {
      const body = JSON.parse(input.body);
      assert.deepEqual(Object.keys(body).sort(), ["minimumPlayerLevel", "startLineId", "title", "version"]);
      if (body.version !== current.version)
        return Response.json({ detail: "Диалог уже изменился." }, { status: 409 });
      writes.push(body);
      Object.assign(current, body, { version: String(++version).padStart(64, "0") });
      return new Response(null, { status: 204 });
    }
    if (path === "/api/dialogues/1") return Response.json(current);
    if (path === "/api/characters") return Response.json({ items: [{ id: 1, name: "Персонаж" }], nextBeforeId: null });
    if (path === "/api/dialogues/1/lines" || path.startsWith("/api/dialogues/1/lines?"))
      return Response.json({ items: path.includes("branchesOnly") ? [] : [line], nextBeforeId: null });
    if (path === "/api/dialogues/1/lines/1/context")
      return Response.json({ line, dialogue: current, incoming: [], outgoing: [], deleteVersion: line.version });
    throw new Error(`Неожиданный запрос: ${input.method} ${path}`);
  };
  try {
    await import("../../src/DialogueSystem.Web/wwwroot/editor.js");
    const rename = document.getElementById("rename-dialogue");
    rename.elements.title.value = "Новое название";
    await rename.onsubmit();
    assert.deepEqual(writes[0], { title: "Новое название", startLineId: 1, minimumPlayerLevel: 2, version: "0".repeat(64) });
    const minimum = document.getElementById("minimum-level");
    minimum.elements.minimumPlayerLevel.value = "5";
    await minimum.onsubmit();
    await document.getElementById("clear-start").onclick();
    await document.getElementById("set-start").onclick();
    assert.equal(writes.length, 4);
    assert.deepEqual(writes.map(body => [body.title, body.startLineId, body.minimumPlayerLevel]), [
      ["Новое название", 1, 2], ["Новое название", 1, 5], ["Новое название", null, 5], ["Новое название", 1, 5]
    ]);
    current.version = "F".repeat(64);
    rename.elements.title.value = "Несохранённое название";
    await rename.onsubmit();
    assert.equal(writes.length, 4);
    assert.equal(rename.elements.title.value, "Несохранённое название");
    assert.match(document.getElementById("status").textContent, /уже изменился/);
  } finally { globalThis.fetch = oldFetch; dom.window.close(); }
});
