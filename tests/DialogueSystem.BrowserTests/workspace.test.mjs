import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";

test("keeps the workspace locked until all pending operations finish", async () => {
  const dom = new JSDOM(`<form id="login"></form><div id="workspace"></div>
    <details id="account-admin"></details><form id="new-account"></form>
    <form id="change-password"></form><details id="my-account"></details>
    <ul id="accounts-list"></ul><button id="refresh-accounts"></button>
    <p id="status"></p><button id="logout"></button>`);
  globalThis.document = dom.window.document;
  const { run } = await import("../../src/DialogueSystem.Web/wwwroot/workspace.js");
  const first = Promise.withResolvers();
  const second = Promise.withResolvers();
  const a = run(() => first.promise)();
  const b = run(() => second.promise)();
  try {
    first.resolve();
    await a;
    assert.equal(document.getElementById("workspace").inert, true);
  } finally {
    second.resolve();
    await b;
    assert.equal(document.getElementById("workspace").inert, false);
    dom.window.close();
  }
});
