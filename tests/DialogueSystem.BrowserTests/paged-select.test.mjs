import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";

test("paged selectors keep an off-page selection and ignore late or detached responses", async () => {
  const dom = new JSDOM(`<form id="login"></form><div id="workspace"></div>
    <details id="account-admin"></details><form id="new-account"></form><form id="change-password"></form>
    <details id="my-account"></details><ul id="accounts-list"></ul><button id="refresh-accounts"></button>
    <p id="status"></p><button id="logout"></button>
    <div id="picker"><select></select><input type="search"><button></button></div>`, { url: "https://example.test" });
  globalThis.document = dom.window.document;
  globalThis.window = dom.window;
  globalThis.location = dom.window.location;
  globalThis.Option = dom.window.Option;
  const previousFetch = globalThis.fetch;
  const requests = [];
  globalThis.fetch = async path => {
    requests.push(path);
    if (path === "/items/99") return Response.json({ id: 99, name: "Текущий вне страницы" });
    return Response.json(path.includes("before=")
      ? { items: [{ id: 2, name: "Два" }, { id: 1, name: "Один" }], nextBeforeId: null }
      : { items: [{ id: 4, name: "Четыре" }, { id: 3, name: "Три" }], nextBeforeId: 3 });
  };
  try {
    const { createPagedSelect } = await import("../../src/DialogueSystem.Web/wwwroot/paged-select.js");
    const host = document.getElementById("picker");
    const select = host.querySelector("select");
    const search = host.querySelector("input");
    const more = host.querySelector("button");
    const picker = createPagedSelect({ select, search, more, endpoint: () => "/items/page", itemEndpoint: id => `/items/${id}`, label: item => item.name });
    await picker.refresh(99);
    assert.equal(select.value, "99");
    assert.equal(select.options.length, 3);
    await more.onclick();
    assert.equal(select.value, "99");
    assert.equal(select.options.length, 5);
    assert.equal(more.hidden, true);
    const slow = Promise.withResolvers();
    const fast = Promise.withResolvers();
    let number = 0;
    globalThis.fetch = () => ++number === 1 ? slow.promise : fast.promise;
    search.value = "старый запрос";
    const a = picker.refresh();
    search.value = "новый запрос";
    const b = picker.refresh();
    fast.resolve(Response.json({ items: [{ id: 8, name: "Свежий результат" }], nextBeforeId: null }));
    await b;
    slow.resolve(Response.json({ items: [{ id: 7, name: "Устаревший результат" }], nextBeforeId: null }));
    await a;
    assert.deepEqual([...select.options].map(option => option.value), ["99", "8"]);
    host.remove();
    assert.equal(await picker.refresh(), null);
    assert.equal(number, 2);
  } finally {
    globalThis.fetch = previousFetch;
    dom.window.close();
  }
});
