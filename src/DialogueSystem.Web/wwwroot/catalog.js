import { api, byId, dialogueUrl, initializeWorkspace, run } from "./workspace.js";

const filters = byId("catalog-filters");
const items = byId("catalog-items");
const result = byId("catalog-result");
let dialogues = [];
let cursor;
let generation = 0;
let filterTimer;

function card(dialogue) {
  const item = document.createElement("li");
  const heading = document.createElement("h3");
  const link = document.createElement("a");
  link.href = dialogueUrl(dialogue.id);
  link.textContent = dialogue.title;
  heading.append(link);

  const details = document.createElement("p");
  const start = dialogue.hasStart ? "Старт задан" : "Без стартовой реплики";
  details.textContent = dialogue.minimumPlayerLevel == null
    ? start
    : `${start} · уровень от ${dialogue.minimumPlayerLevel}`;
  details.className = "catalog-detail";

  const statistics = document.createElement("dl");
  for (const [label, value] of [
    ["Реплики", dialogue.lineCount],
    ["Развилки", dialogue.branchCount],
    ["Концовки", dialogue.endingCount]
  ]) {
    const group = document.createElement("div");
    const name = document.createElement("dt");
    const count = document.createElement("dd");
    name.textContent = label;
    count.textContent = value;
    group.append(name, count);
    statistics.append(group);
  }
  const analysis = document.createElement("a");
  analysis.href = `${dialogueUrl(dialogue.id)}/analysis`;
  analysis.textContent = "Маршруты";
  analysis.setAttribute("aria-label", `Маршруты диалога «${dialogue.title}»`);
  item.append(heading, details, statistics, analysis);
  return item;
}

function renderCatalog() {
  items.replaceChildren(...dialogues.map(card));
  byId("catalog-more").hidden = cursor == null;
  if (dialogues.length === 0) {
    result.textContent = filters.elements.q.value || filters.elements.start.value
      ? "По выбранным фильтрам ничего не найдено." : "Диалогов пока нет. Создайте новый или загрузите JSON.";
  } else {
    result.textContent = `Показано ${dialogues.length}${cursor == null ? "" : " · есть ещё"}`;
  }
}

async function loadCatalog(before) {
  const request = ++generation;
  const query = new URLSearchParams();
  if (filters.elements.q.value.trim()) query.set("q", filters.elements.q.value.trim());
  if (filters.elements.start.value) query.set("hasStart", filters.elements.start.value === "set");
  if (before != null) query.set("before", before);
  const page = await api(`/api/dialogues/page?${query}`);
  if (request !== generation) return;
  if (before == null) dialogues = page.items;
  else dialogues.push(...page.items);
  cursor = page.nextBeforeId;
  renderCatalog();
}

function filterCatalog(immediate = false) {
  const url = new URL(location.href);
  for (const name of ["q", "start"]) {
    const value = filters.elements[name].value.trim();
    if (value) url.searchParams.set(name, value);
    else url.searchParams.delete(name);
  }
  history.replaceState(null, "", url);
  generation++;
  clearTimeout(filterTimer);
  if (immediate) run(() => loadCatalog())();
  else filterTimer = setTimeout(run(() => loadCatalog()), 250);
}

filters.addEventListener("input", () => filterCatalog());
filters.onsubmit = event => {
  event.preventDefault();
  filterCatalog(true);
};
byId("catalog-more").onclick = run(() => loadCatalog(cursor));
byId("new-dialogue").onsubmit = run(async event => {
  const input = Object.fromEntries(new FormData(event.currentTarget));
  const created = await api("/api/dialogues", "POST", input);
  location.assign(dialogueUrl(created.id));
});
byId("import-dialogue").onsubmit = run(async event => {
  const form = event.currentTarget;
  const file = form.elements.file.files[0];
  if (!file) throw new Error("Выберите файл JSON.");
  const maximumBytes = Number(form.dataset.maximumBytes);
  if (file.size > maximumBytes)
    throw new Error(`Размер файла превышает допустимые ${maximumBytes / (1024 * 1024)} МиБ.`);
  const raw = await file.text();
  let document;
  try {
    document = JSON.parse(raw.replace(/^\uFEFF/, ""));
  } catch (error) {
    if (!(error instanceof SyntaxError)) throw error;
    throw new Error("Не удалось прочитать JSON. Проверьте содержимое файла.");
  }
  const created = await api("/api/dialogues/import", "POST", document);
  location.assign(dialogueUrl(created.id));
});

await initializeWorkspace(async () => {
  const query = new URL(location.href).searchParams;
  filters.elements.q.value = query.get("q") ?? "";
  filters.elements.start.value = query.get("start") ?? "";
  if (filters.elements.start.selectedIndex < 0) filters.elements.start.value = "";
  await loadCatalog();
});
