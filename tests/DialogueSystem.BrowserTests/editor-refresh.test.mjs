import { test } from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { setTimeout as delay } from "node:timers/promises";
import { JSDOM } from "jsdom";

test("line saves keep loaded pages and filters while refreshing versions with bounded requests", async () => {
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
  const requests = [];
  let version = 0;
  let conflict = false;
  const line = { id: 30, speakerId: 1, speaker: "Персонаж", text: "Исходный текст", isEnding: false,
    isStoryBranch: false, version: String(version).padStart(64, "0") };
  const current = { id: 1, title: "Редактор", startLineId: 30, minimumPlayerLevel: null,
    version: line.version, deleteVersion: "B".repeat(64) };
  const reference = id => ({ ...line, id, text: id === 30 ? line.text : `Совпадение ${id}` });
  globalThis.fetch = async (route, input = {}) => {
    requests.push(`${input.method ?? "GET"} ${route}`);
    if (route === "/api/accounts/me") return Response.json({ id: 1, role: "Editor" });
    if (route === "/api/accounts/csrf") return Response.json({ token: "csrf" });
    if (route === "/api/characters") return Response.json({ items: [{ id: 1, name: "Персонаж" }], nextBeforeId: null });
    if (route === "/api/dialogues/1/lines/30" && input.method === "PUT") {
      const body = JSON.parse(input.body);
      assert.equal(body.version, line.version);
      if (conflict) return Response.json({ detail: "Реплика изменилась в другой вкладке." }, { status: 409 });
      Object.assign(line, body, { version: String(++version).padStart(64, "0") });
      current.version = line.version;
      return new Response(null, { status: 204 });
    }
    if (route === "/api/dialogues/1/lines/30/context")
      return Response.json({ line, dialogue: current, incoming: [], outgoing: [], deleteVersion: current.version });
    if (route === "/api/dialogues/1") return Response.json(current);
    if (route.startsWith("/api/dialogues/1/lines")) {
      const query = new URL(route, "https://example.test").searchParams;
      if (query.has("branchesOnly")) return Response.json({ items: [], nextBeforeId: null });
      const filtered = query.has("q") && query.get("q") !== "";
      let ids = query.has("before") && query.get("before") !== "31" ? [28, 27] : [30, 29];
      if (filtered) ids = [30, 28, 27].filter(id => reference(id).text.includes(query.get("q")));
      return Response.json({ items: ids.map(reference), nextBeforeId: filtered ? null : ids.at(-1) });
    }
    throw new Error(`Неожиданный запрос: ${input.method} ${route}`);
  };
  try {
    await import("../../src/DialogueSystem.Web/wwwroot/editor.js");
    await document.getElementById("lines-more").onclick();
    const form = document.getElementById("line-form");
    form.elements.text.value = "Совпадение после правки";
    form.elements.isStoryBranch.checked = true;
    document.getElementById("speaker-search").value = "Персонаж";
    requests.length = 0;
    await form.onsubmit();
    assert.deepEqual(requests, ["PUT /api/dialogues/1/lines/30", "GET /api/dialogues/1/lines/30/context"]);
    assert.equal(document.querySelectorAll("#lines button").length, 4);
    assert.match(document.getElementById("lines").textContent, /Совпадение после правки/);
    assert.match(document.getElementById("branches").textContent, /№30/);
    assert.equal(document.getElementById("speaker-search").value, "Персонаж");
    assert.equal(document.getElementById("lines-more").hidden, false);

    const search = document.getElementById("line-search");
    search.value = "Совпадение";
    search.oninput();
    await delay(300);
    form.elements.text.value = "Больше не подходит фильтру";
    form.elements.isStoryBranch.checked = false;
    requests.length = 0;
    await form.onsubmit();
    assert.equal(search.value, "Совпадение");
    assert.deepEqual(requests.slice(0, 2), ["PUT /api/dialogues/1/lines/30", "GET /api/dialogues/1/lines/30/context"]);
    assert.equal(requests.length, 3);
    assert.equal(document.querySelectorAll("#lines button").length, 2);
    assert.doesNotMatch(document.getElementById("lines").textContent, /№30/);
    assert.doesNotMatch(document.getElementById("branches").textContent, /№30/);
    assert.equal(document.getElementById("lines-more").hidden, true);

    conflict = true;
    form.elements.text.value = "Несохранённая правка";
    requests.length = 0;
    await form.onsubmit();
    assert.equal(requests.length, 1);
    assert.equal(form.elements.text.value, "Несохранённая правка");
    assert.match(document.getElementById("status").textContent, /другой вкладке/);
  } finally {
    globalThis.fetch = oldFetch;
    dom.window.close();
  }
});
