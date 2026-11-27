import { test } from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { JSDOM } from "jsdom";

test("password reset refreshes the account version before the next role change", async () => {
  const layout = await readFile(new URL("../../src/DialogueSystem.Web/Views/Shared/_Layout.cshtml", import.meta.url), "utf8");
  const dom = new JSDOM(layout);
  for (const name of ["document", "window", "FormData", "Option"])
    globalThis[name] = name === "window" ? dom.window : dom.window[name];
  const previousFetch = globalThis.fetch;
  const account = { id: 2, login: "target", role: "Editor", version: "A".repeat(64) };
  const savedVersions = [];
  globalThis.fetch = async (path, input = {}) => {
    if (path.startsWith("/api/accounts?") || path === "/api/accounts")
      return Response.json({ items: [account], nextBeforeId: null });
    if (path.endsWith("/password")) {
      account.version = "B".repeat(64);
      return new Response(null, { status: 204 });
    }
    if (path.endsWith("/role")) {
      const body = JSON.parse(input.body);
      savedVersions.push(body.version);
      if (body.version !== account.version) return Response.json({ detail: "Устаревшая версия." }, { status: 409 });
      account.role = body.role;
      return new Response(null, { status: 204 });
    }
    if (path.endsWith("/me")) return Response.json({ id: 1, role: "Administrator" });
    throw new Error(`Неожиданный запрос: ${path}`);
  };
  try {
    const { initializeAccountAdministration } = await import("../../src/DialogueSystem.Web/wwwroot/account-admin.js");
    initializeAccountAdministration().show({ id: 1, role: "Administrator" });
    await document.getElementById("refresh-accounts").onclick();
    const reset = document.querySelectorAll("#accounts-list form")[1];
    reset.querySelectorAll("input")[0].value = "Current-password";
    reset.querySelectorAll("input")[1].value = "Temporary-password";
    await reset.onsubmit();
    const role = document.querySelector("#accounts-list form");
    role.querySelector("select").value = "Administrator";
    await role.onsubmit();
    assert.deepEqual(savedVersions, ["B".repeat(64)]);
    assert.equal(account.role, "Administrator");
    assert.match(document.getElementById("status").textContent, /обновлена/);
  } finally {
    globalThis.fetch = previousFetch;
    dom.window.close();
  }
});
