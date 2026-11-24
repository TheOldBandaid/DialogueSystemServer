import { api, byId, dialogueUrl, downloadJson, initializeWorkspace, run } from "./workspace.js";
import { createDialogueMap } from "./dialogue-map.js";
import { initializeRevisions } from "./dialogue-revisions.js";

const editorContent = byId("editor-content");
const renameDialogueForm = byId("rename-dialogue");
const lineForm = byId("line-form");
const minimumLevelForm = byId("minimum-level");
const previewForm = byId("preview-form");
const previewHistory = byId("preview-history");
const previewSessions = byId("preview-sessions");
const historyDate = new Intl.DateTimeFormat("ru-RU", { dateStyle: "short", timeStyle: "medium" });
const state = { dialogue: null, lines: [], characters: [], line: null };

function setOptions(select, records, label, current) {
  select.replaceChildren(...records.map(item => new Option(label(item), item.id)));
  if (current != null) select.value = current;
}

const path = () => `/api/dialogues/${state.dialogue.id}`;
const linePath = () => `${path()}/lines/${state.line.id}`;
const dialogueMap = createDialogueMap(() => api(`${path()}/export`), selectLine);
const refreshRevisions = initializeRevisions(path);
const lineLabel = line => `№${line.id} · ${line.text}`;
const shortLabel = line => {
  const label = lineLabel(line);
  return label.length > 72 ? `${label.slice(0, 71)}…` : label;
};

function linkToLine(lineId, label) {
  const button = document.createElement("button");
  button.type = "button";
  button.textContent = label;
  button.onclick = run(() => selectLine(lineId));
  const item = document.createElement("li");
  item.append(button);
  return item;
}

function listLinks(target, lines, empty) {
  const list = byId(target);
  list.replaceChildren(...lines.map(line => linkToLine(line.id, shortLabel(line))));
  if (lines.length === 0) {
    const item = document.createElement("li");
    item.textContent = empty;
    list.append(item);
  }
}

async function loadDialogue(id, preferredLineId) {
  hideValidation();
  const route = `/api/dialogues/${id}`;
  const [dialogue, lines, branches] = await Promise.all([
    api(route), api(`${route}/lines`), api(`${route}/story-branches`)
  ]);
  if (state.dialogue?.id !== dialogue.id) {
    previewForm.reset();
    resetPreviewHistory();
  }
  state.dialogue = dialogue;
  byId("dialogue-title").textContent = dialogue.title;
  document.title = `${dialogue.title} — Редактор диалога`;
  renameDialogueForm.elements.title.value = dialogue.title;
  renderLevelSettings();
  state.lines = lines;
  listLinks("lines", lines, "Реплик пока нет.");
  listLinks("branches", branches, "Сюжетные развилки не отмечены.");
  const preferred = lines.find(line => line.id === preferredLineId);
  await selectLine(preferred?.id ?? branches[0]?.id ?? dialogue.startLineId ?? lines[0]?.id);
  await dialogueMap.refresh();
}

async function selectLine(id) {
  const context = id == null ? null : await api(`${path()}/lines/${id}/context`);
  state.line = context?.line ?? null;
  const line = state.line;
  dialogueMap.select(line?.id);
  const url = new URL(location.href);
  if (line) url.searchParams.set("line", line.id);
  else url.searchParams.delete("line");
  if (url.href !== location.href) history.replaceState(null, "", url);
  const isStart = line != null && line.id === state.dialogue.startLineId;
  byId("line-heading").textContent = line ? `Реплика №${line.id}${isStart ? " · старт" : ""}` : "Новая реплика";
  setOptions(lineForm.elements.speakerId, state.characters, character => character.name, line?.speakerId);
  lineForm.elements.text.value = line?.text ?? "";
  lineForm.elements.isEnding.checked = line?.isEnding ?? false;
  lineForm.elements.isStoryBranch.checked = line?.isStoryBranch ?? false;
  byId("set-start").hidden = !line || isStart;
  byId("clear-start").hidden = !isStart;
  byId("delete-line").hidden = !line;
  byId("context").hidden = !context;
  if (!context) return;

  byId("incoming").replaceChildren(...context.incoming.map(choice =>
    linkToLine(choice.line.id, `${choice.text} ← ${shortLabel(choice.line)}`)));
  byId("incoming").hidden = context.incoming.length === 0;
  byId("choices").replaceChildren(...context.outgoing.map(choice => choiceForm(choice)));
  byId("new-choice").hidden = line.isEnding;
  byId("new-choice").onclick = run(() => {
    const position = Math.max(0, ...context.outgoing.map(choice => choice.position));
    const form = choiceForm(null, position < 2147483647 ? position + 1 : 1);
    byId("choices").append(form);
    byId("new-choice").hidden = true;
    form.elements.text.focus();
  });
}

function choiceForm(choice, position) {
  const form = document.createElement("form");
  form.className = "choice";
  form.innerHTML = `
    <label>Ответ <input name="text" maxlength="500" required></label>
    <label>Следующая реплика <select name="targetLineId" required></select></label>
    <label>Позиция <input name="position" type="number" min="1" max="2147483647" required></label>
    <details>
      <summary>Состояние сюжета</summary>
      <label>Доступен при состоянии <input name="requiredState" maxlength="40"></label>
      <label>Новое состояние после выбора <input name="nextState" maxlength="40"></label>
    </details>
    <div class="actions">
      <button>Сохранить вариант</button>
      <button type="button" name="target">Открыть цель</button>
      <button type="button" name="remove"></button>
    </div>`;
  const fields = form.elements;
  fields.text.value = choice?.text ?? "";
  fields.position.value = choice?.position ?? position;
  fields.requiredState.value = choice?.requiredState ?? "";
  fields.nextState.value = choice?.nextState ?? "";
  form.querySelector("details").open = Boolean(choice?.requiredState || choice?.nextState);
  setOptions(fields.targetLineId, state.lines, shortLabel, choice?.line.id);
  fields.target.hidden = !choice;
  fields.target.onclick = run(() => selectLine(choice.line.id));
  fields.remove.textContent = choice ? "Удалить вариант" : "Отмена";
  fields.remove.onclick = run(async () => {
    if (choice) {
      await api(`${linePath()}/choices/${choice.id}`, "DELETE");
      hideValidation();
      await selectLine(state.line.id);
      await dialogueMap.refresh();
    } else {
      form.remove();
      byId("new-choice").hidden = false;
    }
  });
  form.onsubmit = run(async () => {
    const base = `${linePath()}/choices`;
    await api(choice ? `${base}/${choice.id}` : base, choice ? "PUT" : "POST", {
      text: fields.text.value,
      targetLineId: Number(fields.targetLineId.value),
      position: Number(fields.position.value),
      requiredState: fields.requiredState.value.trim() || null,
      nextState: fields.nextState.value.trim() || null
    });
    hideValidation();
    await selectLine(state.line.id);
    await dialogueMap.refresh();
  });
  return form;
}

async function openEditor() {
  const id = Number(editorContent.dataset.dialogueId);
  const query = new URL(location.href).searchParams;
  editorContent.hidden = true;
  byId("show-map").hidden = true;
  state.dialogue = null;
  byId("preview-step").replaceChildren();
  state.characters = await api("/api/characters");
  try {
    await loadDialogue(id, Number(query.get("line")));
  } catch (error) {
    if (error.status !== 404) throw error;
    byId("status").textContent = "Диалог не найден. Его можно выбрать в каталоге.";
    return;
  }
  editorContent.hidden = false;
  byId("show-map").hidden = false;
  await refreshRevisions();
  if (location.hash === "#dialogue-map") byId("map-panel").open = true;
}

function renderLevelSettings() {
  const minimum = state.dialogue.minimumPlayerLevel;
  const hasMinimum = minimum != null;
  minimumLevelForm.elements.minimumPlayerLevel.value = minimum ?? "";
  byId("preview-level").hidden = !hasMinimum;
  byId("preview-level-caption").textContent = hasMinimum ? `Уровень игрока (от ${minimum})` : "Уровень игрока";
  previewForm.elements.playerLevel.disabled = !hasMinimum;
  previewForm.elements.playerLevel.required = hasMinimum;
}

function hideValidation() {
  byId("validation-report").hidden = true;
}

function renderValidation(report) {
  const panel = byId("validation-report");
  const issues = byId("validation-issues");
  const unreachable = byId("validation-unreachable");
  const linesById = new Map(state.lines.map(line => [line.id, line]));

  issues.replaceChildren(...report.issues.map(issue => {
    if (issue.lineId != null) return linkToLine(issue.lineId, issue.message);
    const item = document.createElement("li");
    item.textContent = issue.message;
    return item;
  }));
  issues.hidden = report.issues.length === 0;
  byId("validation-unreachable-lines").replaceChildren(...report.unreachableLineIds.map(id => {
    const line = linesById.get(id);
    return linkToLine(id, line ? shortLabel(line) : `Реплика №${id}`);
  }));
  unreachable.hidden = report.unreachableLineIds.length === 0;
  panel.hidden = false;
  byId("validation-summary").textContent = report.issues.length > 0
    ? "Перед предпросмотром нужно исправить ошибки."
    : "Диалог готов к предпросмотру.";
}

function resetPreviewHistory() {
  previewHistory.open = false;
  previewSessions.replaceChildren();
  previewSessions.disabled = true;
  byId("preview-history-steps").replaceChildren();
  byId("preview-history-message").textContent = "";
}

async function loadPreviewHistory(preferredId = previewSessions.value) {
  const sessions = await api(`${path()}/previews`);
  previewSessions.replaceChildren(...sessions.map(session => {
    const timestamp = session.startedAtUnixMilliseconds;
    const date = timestamp == null ? `Сеанс ${session.previewId.slice(0, 8)}` : historyDate.format(timestamp);
    return new Option(`${date} · переходов: ${session.step}`, session.previewId);
  }));
  previewSessions.disabled = sessions.length === 0;
  if (sessions.some(session => session.previewId === preferredId)) previewSessions.value = preferredId;
  await showPreviewHistory(previewSessions.value);
}

async function showPreviewHistory(previewId) {
  const list = byId("preview-history-steps");
  const message = byId("preview-history-message");
  list.replaceChildren();
  message.textContent = "";
  if (!previewId) {
    message.textContent = "Для этого диалога пока нет прохождений.";
    return;
  }

  const history = await api(`${path()}/previews/${previewId}/history`);
  if (history.steps.length === 0) {
    message.textContent = "Этот сеанс создан до появления истории.";
    return;
  }

  const firstStep = history.steps[0].step;
  list.start = firstStep;
  if (firstStep > 0) message.textContent = `В этом сеансе история записана с шага ${firstStep}.`;
  list.replaceChildren(...history.steps.map(step => {
    const item = document.createElement("li");
    const choice = document.createElement("strong");
    choice.textContent = step.choiceText ?? "Начало диалога";
    item.append(choice, renderPreviewLine(step, step.storyState));
    return item;
  }));
}

async function requestPreview(route, body) {
  try {
    const step = await api(route, "POST", body);
    renderStep(step);
    if (previewHistory.open) await loadPreviewHistory(step.previewId);
  } catch (error) {
    if (error.status !== 422 || !error.result?.issues) throw error;
    byId("preview-step").replaceChildren();
    renderValidation(error.result);
    byId("validation-report").scrollIntoView({ block: "nearest" });
  }
}

function renderPreviewLine(line, storyState) {
  const message = document.createElement("div");
  message.className = "preview-line";
  const text = document.createElement("p");
  const ending = line.isEnding ? " · Конец диалога" : "";
  text.textContent = `${line.speaker} — ${line.text}${ending}`;
  message.append(text);
  if (storyState) {
    const caption = document.createElement("small");
    caption.className = "preview-story-state";
    caption.textContent = `Состояние сюжета — ${storyState}`;
    message.append(caption);
  }
  return message;
}

function renderStep(step) {
  const host = byId("preview-step");
  const actions = document.createElement("div");
  actions.className = "actions";
  for (const choice of step.choices) {
    const button = document.createElement("button");
    button.textContent = choice.text;
    button.onclick = run(() => requestPreview(`${path()}/previews/${step.previewId}/choices`, {
      choiceId: choice.id, step: step.step
    }));
    actions.append(button);
  }
  host.replaceChildren(renderPreviewLine(step.line, step.storyState), actions);
}

renameDialogueForm.onsubmit = run(async () => {
  await api(path(), "PUT", { title: renameDialogueForm.elements.title.value });
  await loadDialogue(state.dialogue.id, state.line?.id);
  byId("status").textContent = `Диалог переименован в «${state.dialogue.title}».`;
});
byId("delete-dialogue").onclick = run(async () => {
  const title = state.dialogue.title;
  if (!window.confirm(`Удалить диалог «${title}» вместе с репликами, переходами, версиями и историей прохождений?`)) return;

  await api(path(), "DELETE");
  location.assign("/");
});
byId("copy-dialogue").onclick = run(async () => {
  const data = await api(`${path()}/export`);
  const suffix = " (копия)";
  const title = data.title.length <= 160 - suffix.length ? data.title + suffix : "Копия диалога";
  const created = await api("/api/dialogues/import", "POST", { ...data, title });
  location.assign(dialogueUrl(created.id));
});
byId("export-dialogue").onclick = run(async () => {
  const id = state.dialogue.id;
  const data = await api(`${path()}/export`);
  downloadJson(data, `dialogue-${id}.json`);
});
byId("new-character").onsubmit = run(async event => {
  const form = event.currentTarget;
  const character = await api("/api/characters", "POST", Object.fromEntries(new FormData(form)));
  form.reset();
  state.characters = await api("/api/characters");
  setOptions(lineForm.elements.speakerId, state.characters, item => item.name, character.id);
  byId("status").textContent = `Персонаж «${character.name}» добавлен.`;
});
byId("new-line").onclick = run(() => selectLine(null));
lineForm.onsubmit = run(async () => {
  const fields = lineForm.elements;
  const saved = await api(state.line ? linePath() : `${path()}/lines`, state.line ? "PUT" : "POST", {
    speakerId: Number(fields.speakerId.value),
    text: fields.text.value,
    isEnding: fields.isEnding.checked,
    isStoryBranch: fields.isStoryBranch.checked
  });
  await loadDialogue(state.dialogue.id, saved?.id ?? state.line.id);
});
byId("set-start").onclick = run(async () => {
  await api(`${path()}/start`, "PUT", { lineId: state.line.id });
  await loadDialogue(state.dialogue.id, state.line.id);
});
byId("clear-start").onclick = run(async () => {
  await api(`${path()}/start`, "PUT", { lineId: null });
  await loadDialogue(state.dialogue.id, state.line.id);
});
byId("delete-line").onclick = run(async () => {
  if (window.confirm("Удалить реплику и её исходящие варианты?")) {
    await api(linePath(), "DELETE");
    await loadDialogue(state.dialogue.id);
  }
});
minimumLevelForm.onsubmit = run(async () => {
  const field = minimumLevelForm.elements.minimumPlayerLevel;
  const minimum = field.value === "" ? null : field.valueAsNumber;
  await api(`${path()}/minimum-level`, "PUT", { minimumPlayerLevel: minimum });
  state.dialogue.minimumPlayerLevel = minimum;
  renderLevelSettings();
  byId("status").textContent = minimum == null ? "Ограничение по уровню снято." : "Минимальный уровень сохранён.";
});
byId("validate-dialogue").onclick = run(async () => {
  hideValidation();
  renderValidation(await api(`${path()}/validation`));
});
const refreshHistory = run(() => loadPreviewHistory());
previewHistory.ontoggle = () => {
  if (previewHistory.open) refreshHistory();
};
byId("refresh-preview-history").onclick = refreshHistory;
previewSessions.onchange = run(() => showPreviewHistory(previewSessions.value));

previewForm.onsubmit = run(async () => {
  byId("preview-step").replaceChildren();
  const level = previewForm.elements.playerLevel;
  await requestPreview(`${path()}/previews`, level.disabled ? undefined : { playerLevel: level.valueAsNumber });
});

await initializeWorkspace(openEditor);
