import { api, byId, dialogueUrl, initializeWorkspace, run } from "./workspace.js";
import { createPagedSelect } from "./paged-select.js";

const endpoint = "/api/characters";
const create = byId("create-character");
const rename = byId("rename-character");
const reassign = byId("reassign-speaker");
const reassignmentPanel = byId("reassignment-panel");
const remove = byId("delete-character");
const details = byId("character-details");
const characterLabel = character => `${character.name} · №${character.id}`;
let selected;
const sourcePicker = createPagedSelect({
  select: byId("character-picker"), search: byId("character-search"), more: byId("characters-more"),
  endpoint: () => `${endpoint}/page`, itemEndpoint: id => `${endpoint}/${id}`, label: characterLabel,
  onChange: character => character ? openCharacter(character.id) : null
});
const targetPicker = createPagedSelect({
  select: reassign.elements.targetCharacterId, search: byId("reassign-search"), more: byId("reassign-more"),
  endpoint: () => `${endpoint}/page`, itemEndpoint: id => `${endpoint}/${id}`, label: characterLabel,
  except: () => selected?.id
});

function remember(id) {
  const url = new URL(location.href);
  if (id == null) url.searchParams.delete("character");
  else url.searchParams.set("character", id);
  if (url.href !== location.href) history.replaceState(null, "", url);
}

function usageLink(dialogue) {
  const item = document.createElement("li");
  const link = document.createElement("a");
  link.href = `${dialogueUrl(dialogue.dialogueId)}?line=${dialogue.firstLineId}`;
  link.textContent = dialogue.title;
  item.append(link, ` · реплик ${dialogue.lineCount}`);
  return item;
}

function renderReassignment() {
  reassignmentPanel.hidden = selected.lineCount === 0 || !targetPicker.current();
  if (reassignmentPanel.hidden) reassignmentPanel.open = false;
  const scopes = selected.dialogues.map(dialogue =>
    new Option(`${dialogue.title} · реплик ${dialogue.lineCount}`, dialogue.dialogueId));
  reassign.elements.dialogueId.replaceChildren(...scopes,
    new Option(`Все диалоги · реплик ${selected.lineCount}`, ""));
  byId("character-usage-more").hidden = selected.nextBeforeId == null;
}

async function openCharacter(id) {
  selected = await api(`${endpoint}/${id}/usage`);
  remember(id);
  details.hidden = false;
  byId("characters-empty").hidden = true;
  rename.elements.name.value = selected.name;
  remove.disabled = selected.lineCount > 0;
  byId("character-usage").textContent = selected.lineCount === 0
    ? "Персонаж пока не используется в диалогах."
    : `Реплик ${selected.lineCount} · диалогов ${selected.dialogueCount}. Для удаления сначала смените говорящего.`;
  byId("character-dialogues").replaceChildren(...selected.dialogues.map(usageLink));
  await targetPicker.refresh();
  renderReassignment();
}

async function loadCharacters(preferredId) {
  const character = await sourcePicker.refresh(preferredId);
  byId("characters-empty").hidden = Boolean(character);
  details.hidden = !character;
  if (!character) { selected = null; remember(null); return; }
  await openCharacter(character.id);
}

byId("characters-refresh").onclick = run(() => loadCharacters(selected?.id));
byId("character-usage-more").onclick = run(async () => {
  if (!selected.nextBeforeId) return;
  const page = await api(`${endpoint}/${selected.id}/usage?before=${selected.nextBeforeId}`);
  if (page.version !== selected.version) throw new Error("Использования персонажа изменились. Нажмите «Обновить данные».");
  selected.dialogues.push(...page.dialogues);
  selected.nextBeforeId = page.nextBeforeId;
  byId("character-dialogues").append(...page.dialogues.map(usageLink));
  renderReassignment();
});
create.onsubmit = run(async () => {
  const character = await api(endpoint, "POST", Object.fromEntries(new FormData(create)));
  create.reset();
  await loadCharacters(character.id);
  byId("status").textContent = `Персонаж «${character.name}» создан.`;
});
rename.onsubmit = run(async () => {
  const id = selected.id;
  await api(`${endpoint}/${id}`, "PUT", { ...Object.fromEntries(new FormData(rename)), version: selected.version });
  await loadCharacters(id);
  byId("status").textContent = "Имя персонажа сохранено.";
});
reassign.onsubmit = run(async () => {
  const { id, name, dialogues, lineCount } = selected;
  const target = targetPicker.current();
  if (!target) throw new Error("Выберите нового говорящего.");
  const dialogueId = Number(reassign.elements.dialogueId.value) || null;
  const scope = dialogues.find(dialogue => dialogue.dialogueId === dialogueId);
  const where = scope ? `в диалоге «${scope.title}»` : "во всех диалогах";
  if (!window.confirm(`Заменить говорящего «${name}» на «${target.name}» ${where}? Реплик — ${scope?.lineCount ?? lineCount}.`)) return;
  const result = await api(`${endpoint}/${id}/reassign`, "POST", {
    targetCharacterId: target.id, dialogueId, version: selected.version, targetVersion: target.version
  });
  await loadCharacters(id);
  byId("status").textContent = `Переназначено реплик — ${result.updatedLineCount}.`;
});
remove.onclick = run(async () => {
  const { id, name } = selected;
  if (!window.confirm(`Удалить персонажа «${name}»?`)) return;
  await api(`${endpoint}/${id}`, "DELETE", { version: selected.version });
  sourcePicker.clear();
  await loadCharacters();
  byId("status").textContent = `Персонаж «${name}» удалён.`;
});

await initializeWorkspace(() => loadCharacters(Number(new URL(location.href).searchParams.get("character"))));
