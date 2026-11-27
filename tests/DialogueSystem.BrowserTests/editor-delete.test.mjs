import { test } from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { JSDOM, VirtualConsole } from "jsdom";

test("dialogue deletion refreshes activity before confirmation and still rejects changed graphs and later writes", async () => {
  const layout = await readFile(new URL("../../src/DialogueSystem.Web/Views/Shared/_Layout.cshtml", import.meta.url), "utf8");
  const editor = await readFile(new URL("../../src/DialogueSystem.Web/Views/Pages/Editor.cshtml", import.meta.url), "utf8");
  let navigations = 0;
  const console = new VirtualConsole();
  console.on("jsdomError", error => {
    if (/navigation/i.test(error.message)) navigations++;
    else throw error;
  });
  const dom = new JSDOM(layout.replace("@RenderBody()", editor.replace('data-dialogue-id="@Model"', 'data-dialogue-id="1"')),
    { url: "https://example.test/dialogues/1", virtualConsole: console });
  globalThis.document = dom.window.document;
  globalThis.window = dom.window;
  globalThis.location = dom.window.location;
  globalThis.history = dom.window.history;
  globalThis.FormData = dom.window.FormData;
  globalThis.Option = dom.window.Option;
  globalThis.ResizeObserver = class { observe() {} };
  globalThis.requestAnimationFrame = () => 1;
  globalThis.cancelAnimationFrame = () => {};
  dom.window.HTMLCanvasElement.prototype.getContext = () => null;
  const prompts = [];
  let approved = false;
  dom.window.confirm = message => { prompts.push(message); return approved; };
  const oldFetch = globalThis.fetch;
  const originalVersion = "A".repeat(64);
  let graphVersion = originalVersion;
  let token = "B".repeat(64);
  let writeAfterConfirmation = true;
  const deletionHeaders = [];
  const line = { id: 1, speakerId: 1, speaker: "Персонаж", text: "Конец", isEnding: true, isStoryBranch: false, version: "D".repeat(64) };
  const dialogue = () => ({ id: 1, title: "Проверка", startLineId: 1, minimumPlayerLevel: null, version: graphVersion, deleteVersion: token });
  const revision = { id: 1, label: "Моя версия", author: "editor", lineCount: 1, choiceCount: 0, createdAtUnixMilliseconds: Date.now() };
  globalThis.fetch = async (path, input = {}) => {
    if (path === "/api/accounts/me") return Response.json({ id: 1, login: "editor", role: "Editor" });
    if (path === "/api/accounts/csrf") return Response.json({ token: "csrf" });
    if (path.startsWith("/api/characters/page")) return Response.json({ items: [{ id: 1, name: "Персонаж" }], nextBeforeId: null });
    if (path === "/api/dialogues/1" && input.method === "DELETE") {
      deletionHeaders.push(input.headers["If-Match"]);
      if (writeAfterConfirmation) token = "E".repeat(64);
      return input.headers["If-Match"] === `"${token}"`
        ? new Response(null, { status: 204 })
        : Response.json({ detail: "Данные изменились после подтверждения." }, { status: 412 });
    }
    if (path === "/api/dialogues/1") return Response.json(dialogue());
    if (path.startsWith("/api/dialogues/1/lines/page"))
      return Response.json({ items: path.includes("branchesOnly") ? [] : [line], nextBeforeId: null });
    if (path === "/api/dialogues/1/lines/1/context")
      return Response.json({ line, incoming: [], outgoing: [], dialogue: dialogue(), deleteVersion: line.version, nextIncoming: null, nextOutgoingPosition: null });
    if (path === "/api/dialogues/1/revisions" && input.method === "POST") {
      token = "C".repeat(64);
      return Response.json(revision, { status: 201 });
    }
    if (path === "/api/dialogues/1/revisions") return Response.json({ items: [revision], nextBeforeId: null });
    if (path === "/api/dialogues/1/previews" && input.method === "POST") {
      token = "F".repeat(64);
      return Response.json({ previewId: "session", step: 0, line, choices: [], unreachableLineIds: [] });
    }
    throw new Error(`Неожиданный запрос: ${input.method} ${path}`);
  };
  try {
    await import("../../src/DialogueSystem.Web/wwwroot/editor.js");
    const lineForm = document.getElementById("line-form");
    lineForm.elements.text.value = "Несохранённый текст";
    document.getElementById("revision-panel").open = true;
    const form = document.getElementById("save-revision");
    form.elements.label.value = revision.label;
    await form.onsubmit();
    const remove = document.getElementById("delete-dialogue");
    await remove.onclick();
    assert.equal(deletionHeaders.length, 0);
    assert.equal(lineForm.elements.text.value, "Несохранённый текст");
    approved = true;
    await remove.onclick();
    assert.deepEqual(deletionHeaders, [`"${"C".repeat(64)}"`]);
    assert.match(prompts[0], /изменились версии или история/);
    assert.match(document.getElementById("status").textContent, /после подтверждения/);
    assert.equal(lineForm.elements.text.value, "Несохранённый текст");

    graphVersion = "E".repeat(64);
    await remove.onclick();
    assert.equal(deletionHeaders.length, 1);
    assert.equal(prompts.length, 2);
    assert.match(document.getElementById("status").textContent, /Диалог изменился/);
    assert.equal(lineForm.elements.text.value, "Несохранённый текст");

    graphVersion = originalVersion;
    writeAfterConfirmation = false;
    await document.getElementById("preview-form").onsubmit();
    await remove.onclick();
    assert.equal(deletionHeaders[1], `"${"F".repeat(64)}"`);
    assert.equal(navigations, 1);
    assert.equal(document.getElementById("status").textContent, "");
  } finally {
    globalThis.fetch = oldFetch;
    dom.window.close();
  }
});
