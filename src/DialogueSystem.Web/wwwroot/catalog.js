import { api, byId, dialogueUrl, initializeWorkspace, run } from "./workspace.js";

const filters = byId("catalog-filters");
const items = byId("catalog-items");
const result = byId("catalog-result");
let dialogues = [];

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
  item.append(heading, details, statistics);
  return item;
}

function renderCatalog() {
  const search = filters.elements.q.value.trim().toLocaleLowerCase("ru-RU");
  const start = filters.elements.start.value;
  const visible = dialogues.filter(dialogue => {
    const matchesSearch = dialogue.title.toLocaleLowerCase("ru-RU").includes(search);
    const matchesStart = start === "" || (start === "set") === dialogue.hasStart;
    return matchesSearch && matchesStart;
  });
  items.replaceChildren(...visible.map(card));
  if (dialogues.length === 0) {
    result.textContent = "Диалогов пока нет. Создайте новый или загрузите JSON.";
  } else if (visible.length === 0) {
    result.textContent = "По выбранным фильтрам ничего не найдено.";
  } else {
    result.textContent = `Показано ${visible.length} из ${dialogues.length}`;
  }
}

function filterCatalog() {
  const url = new URL(location.href);
  for (const name of ["q", "start"]) {
    const value = filters.elements[name].value.trim();
    if (value) url.searchParams.set(name, value);
    else url.searchParams.delete(name);
  }
  history.replaceState(null, "", url);
  renderCatalog();
}

filters.addEventListener("input", filterCatalog);
filters.onsubmit = event => {
  event.preventDefault();
  filterCatalog();
};
byId("new-dialogue").onsubmit = run(async event => {
  const input = Object.fromEntries(new FormData(event.currentTarget));
  const created = await api("/api/dialogues", "POST", input);
  location.assign(dialogueUrl(created.id));
});
byId("import-dialogue").onsubmit = run(async event => {
  const raw = await event.currentTarget.elements.file.files[0].text();
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
  dialogues = await api("/api/dialogues/overview");
  renderCatalog();
});
