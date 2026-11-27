import { test } from "node:test";
import assert from "node:assert/strict";
import { api, refreshToken } from "../../src/DialogueSystem.Web/wwwroot/http-client.js";

test("HTTP client sends refreshed CSRF tokens and explicit deletion preconditions", async () => {
  const previousFetch = globalThis.fetch;
  const requests = [];
  globalThis.fetch = async (path, input) => {
    requests.push({ path, ...input });
    return path.endsWith("/csrf") ? Response.json({ token: "current-token" }) : new Response(null, { status: 204 });
  };
  try {
    await refreshToken();
    await api("/resource/1", "DELETE", { version: "A".repeat(64) });
    assert.deepEqual(requests.map(request => request.path), ["/api/accounts/csrf", "/resource/1"]);
    assert.equal(requests[1].headers["If-Match"], `"${"A".repeat(64)}"`);
    assert.equal(requests[1].headers["X-CSRF-TOKEN"], "current-token");
    assert.equal(requests[1].body, undefined);
  } finally { globalThis.fetch = previousFetch; }
});
