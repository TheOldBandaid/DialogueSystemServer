import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";

test("paged selectors keep an off-page selection and ignore late or detached responses", async () => {
  const dom = new JSDOM(`    <div id="picker"><select></select><input type="search"><button></button></div>`, { url: "https://example.test" });
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
    const picker = createPagedSelect({ select, search, more, endpoint: () => "/items", itemEndpoint: id => `/items/${id}`, label: item => item.name });
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
    globalThis.fetch = path => path === "/items/99" ? Response.json({ id: 99, name: "Текущий вне страницы" }) : ++number === 1 ? slow.promise : fast.promise;
    search.value = "старый запрос";
    const a = picker.refresh();
    search.value = "новый запрос";
    const b = picker.refresh();
    fast.resolve(Response.json({ items: [{ id: 8, name: "Свежий результат" }], nextBeforeId: null }));
    await b;
    slow.resolve(Response.json({ items: [{ id: 7, name: "Устаревший результат" }], nextBeforeId: null }));
    await a;
    assert.deepEqual([...select.options].map(option => option.value), ["99", "8"]);

    globalThis.fetch = async path => (path === "/items" || path.startsWith("/items?"))
      ? Response.json({ items: [{ id: 8, name: "Существующий" }], nextBeforeId: null })
      : Response.json({ detail: "Удалено" }, { status: 404 });
    assert.equal((await picker.refresh(99)).id, 8);
    assert.equal(select.value, "8");
    assert.equal([...select.options].some(option => option.value === "99"), false);
    picker.choose({ id: 98, name: "Удалённый без явного выбора" });
    assert.equal((await picker.refresh()).id, 8);

    const oldItem = Promise.withResolvers();
    let itemRequested;
    const requested = new Promise(resolve => { itemRequested = resolve; });
    globalThis.fetch = async path => {
      if (path === "/items" || path.startsWith("/items?")) return Response.json({ items: [{ id: 8, name: "Существующий" }], nextBeforeId: null });
      if (path === "/items/101") { itemRequested(); return oldItem.promise; }
      return Response.json({ id: 102, name: "Новый выбор" });
    };
    const oldRefresh = picker.refresh(101);
    await requested;
    await picker.refresh(102);
    oldItem.resolve(Response.json({ id: 101, name: "Запоздавший выбор" }));
    await oldRefresh;
    assert.equal(select.value, "102");
    assert.equal([...select.options].some(option => option.value === "101"), false);

    globalThis.fetch = async path => (path === "/items" || path.startsWith("/items?"))
      ? Response.json({ items: [], nextBeforeId: null })
      : Response.json({ detail: "Удалено" }, { status: 404 });
    assert.equal(await picker.refresh(102), null);
    assert.equal(select.options.length, 0);
    host.remove();
    assert.equal(await picker.refresh(), null);
    assert.equal(number, 2);
  } finally {
    globalThis.fetch = previousFetch;
    dom.window.close();
  }
});
