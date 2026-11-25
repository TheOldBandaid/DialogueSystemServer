import { api, byId, dialogueUrl, initializeWorkspace, run } from "./workspace.js";

const endpoint = "/api/characters";
const picker = byId("character-picker");
const create = byId("create-character");
const rename = byId("rename-character");
const reassign = byId("reassign-speaker");
const reassignmentPanel = byId("reassignment-panel");
const remove = byId("delete-character");
const details = byId("character-details");
let characters = [];
let selected;

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
  const candidates = characters.filter(character => character.id !== selected.id);
  reassignmentPanel.hidden = selected.lineCount === 0 || candidates.length === 0;
  if (reassignmentPanel.hidden) {
    reassignmentPanel.open = false;
    return;
  }

  const targets = candidates.map(character => new Option(`${character.name} · №${character.id}`, character.id));
  reassign.elements.targetCharacterId.replaceChildren(...targets);
  const scopes = selected.dialogues.map(dialogue =>
    new Option(`${dialogue.title} · реплик ${dialogue.lineCount}`, dialogue.dialogueId));
  reassign.elements.dialogueId.replaceChildren(...scopes,
    new Option(`Все диалоги · реплик ${selected.lineCount}`, ""));
}

async function openCharacter(id) {
  selected = await api(`${endpoint}/${id}/usage`);
  remember(id);
  rename.elements.name.value = selected.name;
  remove.disabled = selected.lineCount > 0;
  byId("character-usage").textContent = selected.lineCount === 0
    ? "Персонаж пока не используется в диалогах."
    : `Реплик ${selected.lineCount} · диалогов ${selected.dialogues.length}. Чтобы удалить персонажа, сначала смените говорящего в этих репликах.`;
  byId("character-dialogues").replaceChildren(...selected.dialogues.map(usageLink));
  renderReassignment();
}

async function loadCharacters(preferredId) {
  characters = await api(endpoint);
  picker.replaceChildren(...characters.map(character => new Option(`${character.name} · №${character.id}`, character.id)));
  byId("characters-empty").hidden = characters.length > 0;
  details.hidden = characters.length === 0;
  if (characters.length === 0) {
    selected = null;
    remember(null);
    return;
  }

  const preferred = characters.find(character => character.id === preferredId) ?? characters[0];
  picker.value = preferred.id;
  await openCharacter(preferred.id);
}

picker.onchange = run(() => openCharacter(Number(picker.value)));
create.onsubmit = run(async () => {
  const character = await api(endpoint, "POST", Object.fromEntries(new FormData(create)));
  create.reset();
  await loadCharacters(character.id);
  byId("status").textContent = `Персонаж «${character.name}» создан.`;
});
rename.onsubmit = run(async () => {
  const id = selected.id;
  await api(`${endpoint}/${id}`, "PUT", Object.fromEntries(new FormData(rename)));
  await loadCharacters(id);
  byId("status").textContent = "Имя персонажа сохранено.";
});
reassign.onsubmit = run(async () => {
  const { id, name, dialogues, lineCount } = selected;
  const targetCharacterId = Number(reassign.elements.targetCharacterId.value);
  const target = characters.find(character => character.id === targetCharacterId);
  const dialogueId = Number(reassign.elements.dialogueId.value) || null;
  const scope = dialogues.find(dialogue => dialogue.dialogueId === dialogueId);
  const where = scope ? `в диалоге «${scope.title}»` : "во всех диалогах";
  const count = scope?.lineCount ?? lineCount;
  if (!window.confirm(`Заменить говорящего «${name}» на «${target.name}» ${where}? Реплик к переносу — ${count}.`)) return;

  const result = await api(`${endpoint}/${id}/reassign`, "POST", { targetCharacterId, dialogueId });
  await openCharacter(id);
  byId("status").textContent = `Переназначено реплик — ${result.updatedLineCount}.`;
});
remove.onclick = run(async () => {
  const { id, name } = selected;
  if (!window.confirm(`Удалить персонажа «${name}»?`)) return;

  await api(`${endpoint}/${id}`, "DELETE");
  await loadCharacters();
  byId("status").textContent = `Персонаж «${name}» удалён.`;
});

await initializeWorkspace(() => loadCharacters(Number(new URL(location.href).searchParams.get("character"))));
