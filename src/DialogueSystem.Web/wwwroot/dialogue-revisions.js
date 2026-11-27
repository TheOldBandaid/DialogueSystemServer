import { api } from "./http-client.js";
import { byId, dialogueUrl, downloadJson, run } from "./ui.js";

export function initializeRevisions(path, beforeCopy = () => true, copied = () => {}) {
  const panel = byId("revision-panel");
  const form = byId("save-revision");
  const list = byId("revision-list");
  const more = byId("revision-more");
  const date = new Intl.DateTimeFormat("ru-RU", { dateStyle: "short", timeStyle: "medium" });
  let cursor;

  function entry(version) {
    const item = document.createElement("li");
    const label = document.createElement("strong");
    label.textContent = version.label;
    const meta = document.createElement("small");
    meta.textContent = `${date.format(version.createdAtUnixMilliseconds)} · ${version.author} · реплик ${version.lineCount} · переходов ${version.choiceCount}`;
    const actions = document.createElement("div");
    actions.className = "actions";
    const exportButton = document.createElement("button");
    exportButton.type = "button";
    exportButton.textContent = "Скачать JSON версии";
    exportButton.onclick = run(async () => {
      const snapshot = await api(`${path()}/revisions/${version.id}/export`);
      downloadJson(snapshot, `dialogue-version-${version.id}.json`);
    });
    const restore = document.createElement("button");
    restore.type = "button";
    restore.textContent = "Открыть как копию";
    restore.onclick = run(async () => {
      if (!beforeCopy()) return;
      const copy = await api(`${path()}/revisions/${version.id}/copy`, "POST", {
        cloneCharacters: byId("revision-copy-characters").checked
      });
      copied();
      location.assign(dialogueUrl(copy.id));
    });
    actions.append(exportButton, restore);
    item.append(label, meta, actions);
    return item;
  }

  async function loadPage(before) {
    if (!panel.open) return;

    const query = before == null ? "" : `?before=${before}`;
    const page = await api(`${path()}/revisions${query}`);
    const rows = page.items.map(entry);
    if (before == null) list.replaceChildren(...rows);
    else list.append(...rows);
    byId("revision-empty").hidden = list.childElementCount !== 0;
    cursor = page.nextBeforeId;
    more.hidden = cursor == null;
  }

  const refresh = () => loadPage();
  panel.ontoggle = () => {
    if (panel.open) run(refresh)();
  };
  more.onclick = run(() => loadPage(cursor));
  form.onsubmit = run(async () => {
    const input = Object.fromEntries(new FormData(form));
    const saved = await api(`${path()}/revisions`, "POST", input);
    form.reset();
    await refresh();
    byId("status").textContent = `Версия «${saved.label}» сохранена.`;
  });
  return refresh;
}
