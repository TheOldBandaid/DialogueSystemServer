import { api, run } from "./workspace.js";

export function createPagedSelect({ select, search, more, endpoint, itemEndpoint, label,
  except = () => null, initialPage, selectedRecord, onChange, readItem = value => value }) {
  const records = new Map();
  const loaded = new Set();
  let cursor = null;
  let selected = selectedRecord ?? null;
  let generation = 0;
  let timer;

  function render() {
    const excluded = except();
    const rows = [...records.values()].filter(record => record.id !== excluded);
    if (!selected || selected.id === excluded) selected = rows[0] ?? null;
    select.replaceChildren(...rows.map(record => new Option(
      `${selected?.id === record.id && !loaded.has(record.id) ? "Текущий: " : ""}${label(record)}`, record.id)));
    if (selected) select.value = String(selected.id);
    more.hidden = cursor == null;
  }

  function accept(page, reset) {
    if (reset) {
      records.clear();
      loaded.clear();
      if (selected) records.set(selected.id, selected);
    }
    for (const record of page.items) { records.set(record.id, record); loaded.add(record.id); }
    cursor = page.nextBeforeId;
    render();
  }

  async function load(reset, notify = false) {
    if (!select.isConnected) return false;
    const request = ++generation;
    const previousId = current()?.id;
    const url = new URL(endpoint(), location.origin);
    const query = search.value.trim();
    if (query) url.searchParams.set("q", query);
    if (!reset && cursor != null) url.searchParams.set("before", cursor);
    if (except() != null) url.searchParams.set("except", except());
    const page = await api(url.pathname + url.search);
    if (request !== generation || !select.isConnected) return false;
    accept(page, reset);
    if (notify && previousId !== current()?.id && onChange) await onChange(current());
    return true;
  }

  async function refresh(id) {
    if (!await load(true)) return null;
    if (id != null && id > 0) {
      let record = records.get(id);
      if (!record || !loaded.has(id)) {
        try { record = readItem(await api(itemEndpoint(id))); }
        catch (error) { if (error.status !== 404) throw error; }
      }
      if (record) { records.set(id, record); selected = record; render(); }
    }
    return current();
  }

  function choose(record) {
    selected = record;
    if (record) records.set(record.id, record);
    render();
  }

  function current() { return records.get(Number(select.value)) ?? null; }
  function clear() {
    clearTimeout(timer);
    generation++;
    selected = null;
    records.clear(); loaded.clear(); cursor = null;
    render();
  }
  search.oninput = () => {
    clearTimeout(timer);
    timer = setTimeout(run(() => load(true, true)), 250);
  };
  more.onclick = run(() => load(false));
  if (onChange) select.onchange = run(async () => { selected = current(); await onChange(selected); });
  else select.onchange = () => { selected = current(); };
  if (initialPage) accept(initialPage, true);
  else render();
  return { refresh, choose, current, clear };
}
