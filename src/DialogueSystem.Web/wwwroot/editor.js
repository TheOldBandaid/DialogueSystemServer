import { api, byId, dialogueUrl, downloadJson, initializeWorkspace, run } from "./workspace.js";
import { createDialogueMap } from "./dialogue-map.js";
import { initializeRevisions } from "./dialogue-revisions.js";
import { initializeCleanup } from "./dialogue-cleanup.js";
import { createDraftGuard } from "./draft-guard.js";
import { createPagedSelect } from "./paged-select.js";

const editorContent = byId("editor-content");
const renameDialogueForm = byId("rename-dialogue");
const lineForm = byId("line-form");
const minimumLevelForm = byId("minimum-level");
const previewForm = byId("preview-form");
const previewHistory = byId("preview-history");
const previewSessions = byId("preview-sessions");
const historyDate = new Intl.DateTimeFormat("ru-RU", { dateStyle: "short", timeStyle: "medium" });
const state = { dialogue: null, lines: [], branches: [], line: null, deleteVersion: null, targetPage: null };
let contextPage;
const drafts = createDraftGuard(editorContent);
let sessionCursor;
let historyCursor;
let linesCursor;
let branchesCursor;
let linesGeneration = 0;
let linesTimer;

const path = () => `/api/dialogues/${state.dialogue.id}`;
const linePath = () => `${path()}/lines/${state.line.id}`;
const characterLabel = character => `${character.name} · №${character.id}`;
const speakerPicker = createPagedSelect({
  select: lineForm.elements.speakerId, search: byId("speaker-search"), more: byId("speakers-more"),
  endpoint: () => "/api/characters/page", itemEndpoint: id => `/api/characters/${id}`, label: characterLabel
});
const dialogueMap = createDialogueMap(() => api(`${path()}/map`), selectLine);
const refreshRevisions = initializeRevisions(path, () => drafts.confirmDiscard(), () => drafts.reset());
const cleanup = initializeCleanup(path, async () => {
  resetPreviewHistory();
  await refreshRevisions();
});
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
    api(route), api(`${route}/lines/page`), api(`${route}/lines/page?branchesOnly=true`)
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
  state.lines = lines.items;
  state.branches = branches.items;
  state.targetPage = lines;
  linesCursor = lines.nextBeforeId;
  branchesCursor = branches.nextBeforeId;
  byId("line-search").value = "";
  linesGeneration++;
  listLinks("lines", state.lines, "Реплик пока нет.");
  listLinks("branches", state.branches, "Сюжетные развилки не отмечены.");
  byId("lines-more").hidden = linesCursor == null;
  byId("branches-more").hidden = branchesCursor == null;
  const fallback = state.branches[0]?.id ?? dialogue.startLineId ?? state.lines[0]?.id;
  try { await selectLine(preferredLineId > 0 ? preferredLineId : fallback, true); }
  catch (error) {
    if (error.status !== 404 || !(preferredLineId > 0)) throw error;
    await selectLine(fallback, true);
  }
  await dialogueMap.refresh();
}

async function selectLine(id, discardConfirmed = false, preferredPosition) {
  if (!discardConfirmed && !drafts.confirmDiscard()) return;
  const after = preferredPosition > 20 ? preferredPosition - 20 : null;
  let context;
  if (id != null && after) {
    const fresh = await api(path());
    context = await api(`${path()}/lines/${id}/context?outgoingAfter=${after}&version=${fresh.version}`);
  } else context = id == null ? null : await api(`${path()}/lines/${id}/context`);
  contextPage = context;
  if (context?.dialogue) {
    state.dialogue = context.dialogue;
    byId("dialogue-title").textContent = state.dialogue.title;
    renameDialogueForm.elements.title.value = state.dialogue.title;
    renderLevelSettings();
  }
  state.deleteVersion = context?.deleteVersion;
  state.line = context?.line ?? null;
  const line = state.line;
  dialogueMap.select(line?.id);
  const url = new URL(location.href);
  if (line) url.searchParams.set("line", line.id);
  else url.searchParams.delete("line");
  if (url.href !== location.href) history.replaceState(null, "", url);
  const isStart = line != null && line.id === state.dialogue.startLineId;
  byId("line-heading").textContent = line ? `Реплика №${line.id}${isStart ? " · старт" : ""}` : "Новая реплика";
  byId("speaker-search").value = "";
  await speakerPicker.refresh(line?.speakerId);
  lineForm.elements.text.value = line?.text ?? "";
  lineForm.elements.isEnding.checked = line?.isEnding ?? false;
  lineForm.elements.isStoryBranch.checked = line?.isStoryBranch ?? false;
  byId("set-start").hidden = !line || isStart;
  byId("clear-start").hidden = !isStart;
  byId("delete-line").hidden = !line;
  byId("context").hidden = !context;
  if (!context) {
    byId("choices").replaceChildren();
    byId("choices-first").hidden = true;
    byId("choices-more").hidden = true;
    byId("incoming-more").hidden = true;
    drafts.reset(lineForm, renameDialogueForm, minimumLevelForm);
    return;
  }

  byId("incoming").replaceChildren(...context.incoming.map(choice =>
    linkToLine(choice.line.id, `${choice.text} ← ${shortLabel(choice.line)}`)));
  byId("incoming").hidden = context.incoming.length === 0;
  byId("incoming-more").hidden = context.nextIncoming == null;
  byId("choices").replaceChildren(...context.outgoing.map((choice, index) =>
    choiceForm(choice, undefined, index === 0 && !after, index === context.outgoing.length - 1 && context.nextOutgoingPosition == null)));
  byId("choices-first").hidden = !after;
  byId("choices-more").hidden = context.nextOutgoingPosition == null;
  byId("new-choice").hidden = line.isEnding;
  byId("new-choice").onclick = run(() => {
    const form = choiceForm(null, context.suggestedChoicePosition);
    byId("choices").append(form);
    drafts.watch(form);
    byId("new-choice").hidden = true;
    form.elements.text.focus();
  });
  drafts.reset(lineForm, renameDialogueForm, minimumLevelForm, ...byId("choices").querySelectorAll("form"));
}

function choiceForm(choice, position, first = false, last = false) {
  const form = document.createElement("form");
  form.className = "choice";
  form.innerHTML = `
    <label>Ответ <input name="text" maxlength="500" required></label>
    <label>Следующая реплика <select name="targetLineId" required></select></label>
    <label>Поиск цели <input type="search" class="target-search" maxlength="160"></label>
    <button type="button" class="targets-more" hidden>Показать ещё цели</button>
    <label>Позиция <input name="position" type="number" min="1" max="2147483647" required></label>
    <details>
      <summary>Состояние сюжета</summary>
      <label>Доступен при состоянии <input name="requiredState" maxlength="40"></label>
      <label>Новое состояние после выбора <input name="nextState" maxlength="40"></label>
    </details>
    <div class="actions">
      <button>Сохранить вариант</button>
      <button type="button" name="target">Открыть цель</button>
      <button type="button" name="moveUp">Выше</button>
      <button type="button" name="moveDown">Ниже</button>
      <button type="button" name="removeChoice"></button>
    </div>`;
  const fields = form.elements;
  fields.text.value = choice?.text ?? "";
  fields.position.value = choice?.position ?? position;
  fields.requiredState.value = choice?.requiredState ?? "";
  fields.nextState.value = choice?.nextState ?? "";
  form.querySelector("details").open = Boolean(choice?.requiredState || choice?.nextState);
  createPagedSelect({
    select: fields.targetLineId, search: form.querySelector(".target-search"), more: form.querySelector(".targets-more"),
    endpoint: () => `${path()}/lines/page`, itemEndpoint: id => `${path()}/lines/${id}/context`,
    readItem: context => context.line, label: shortLabel, initialPage: state.targetPage,
    selectedRecord: choice?.line ?? state.targetPage.items[0] ?? state.line
  });
  fields.target.hidden = !choice;
  fields.target.onclick = run(() => selectLine(choice.line.id));
  for (const [button, direction, boundary] of [[fields.moveUp, -1, first], [fields.moveDown, 1, last]]) {
    button.hidden = !choice;
    button.disabled = boundary;
    button.onclick = run(async () => {
      if (!drafts.confirmDiscard()) return;
      await api(`${linePath()}/choices/${choice.id}/move`, "POST", { direction, version: choice.version });
      hideValidation();
      const moved = await api(`${linePath()}/choices/${choice.id}`);
      await selectLine(state.line.id, true, moved.position);
      await dialogueMap.refresh();
    });
  }
  fields.removeChoice.textContent = choice ? "Удалить вариант" : "Отмена";
  fields.removeChoice.onclick = run(async () => {
    if (choice) {
      if (!drafts.confirmDiscard(form)) return;
      await api(`${linePath()}/choices/${choice.id}`, "DELETE", { version: choice.version });
      hideValidation();
      await selectLine(state.line.id, true);
      await dialogueMap.refresh();
    } else {
      form.remove();
      byId("new-choice").hidden = false;
    }
  });
  form.onsubmit = run(async () => {
    if (!drafts.confirmDiscard(form)) return;
    const base = `${linePath()}/choices`;
    await api(choice ? `${base}/${choice.id}` : base, choice ? "PUT" : "POST", {
      text: fields.text.value,
      targetLineId: Number(fields.targetLineId.value),
      position: Number(fields.position.value),
      requiredState: fields.requiredState.value.trim() || null,
      nextState: fields.nextState.value.trim() || null,
      version: choice?.version
    });
    hideValidation();
    await selectLine(state.line.id, true, Number(fields.position.value));
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
  dialogueMap.reset();
  cleanup.reset();
  byId("preview-step").replaceChildren();
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
  sessionCursor = null;
  historyCursor = null;
  byId("preview-sessions-more").hidden = true;
  byId("preview-history-more").hidden = true;
  previewHistory.open = false;
  previewSessions.replaceChildren();
  previewSessions.disabled = true;
  byId("preview-history-steps").replaceChildren();
  byId("preview-history-message").textContent = "";
}

async function loadPreviewHistory(preferredId = previewSessions.value) {
  const page = await api(`${path()}/previews/page`);
  const sessions = page.items;
  sessionCursor = page.nextBeforeId;
  byId("preview-sessions-more").hidden = sessionCursor == null;
  previewSessions.replaceChildren(...sessions.map(previewSessionOption));
  previewSessions.disabled = sessions.length === 0;
  if (sessions.some(session => session.previewId === preferredId)) previewSessions.value = preferredId;
  await showPreviewHistory(previewSessions.value);
}

function previewSessionOption(session) {
  const timestamp = session.startedAtUnixMilliseconds;
  const date = timestamp == null ? `Сеанс ${session.previewId.slice(0, 8)}` : historyDate.format(timestamp);
  return new Option(`${date} · переходов: ${session.step}`, session.previewId);
}

async function showPreviewHistory(previewId) {
  const list = byId("preview-history-steps");
  const message = byId("preview-history-message");
  list.replaceChildren();
  message.textContent = "";
  historyCursor = null;
  byId("preview-history-more").hidden = true;
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
  list.replaceChildren(...history.steps.map(previewHistoryRow));
  historyCursor = history.nextAfterStep;
  byId("preview-history-more").hidden = historyCursor == null;
}

function previewHistoryRow(step) {
  const item = document.createElement("li");
  const choice = document.createElement("strong");
  choice.textContent = step.choiceText ?? "Начало диалога";
  item.append(choice, renderPreviewLine(step, step.storyState));
  return item;
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
  if (!drafts.confirmDiscard(renameDialogueForm)) return;
  await api(path(), "PUT", { title: renameDialogueForm.elements.title.value, version: state.dialogue.version });
  await loadDialogue(state.dialogue.id, state.line?.id);
  byId("status").textContent = `Диалог переименован в «${state.dialogue.title}».`;
});
byId("delete-dialogue").onclick = run(async () => {
  const current = await api(path());
  if (current.version !== state.dialogue.version)
    throw new Error("Диалог изменился. Обновите страницу и проверьте его перед удалением.");
  const changed = current.deleteVersion !== state.dialogue.deleteVersion
    ? "После открытия диалога изменились версии или история прохождений.\n" : "";
  if (!window.confirm(`${changed}Удалить диалог «${current.title}» вместе с репликами, переходами, версиями и историей прохождений?`)) return;

  await api(path(), "DELETE", { version: current.deleteVersion });
  drafts.reset();
  location.assign("/");
});
byId("copy-dialogue").onclick = run(async () => {
  if (!drafts.confirmDiscard()) return;
  const created = await api(`${path()}/copy`, "POST", { cloneCharacters: byId("copy-characters").checked });
  drafts.reset();
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
  speakerPicker.choose(character);
  byId("status").textContent = `Персонаж «${character.name}» добавлен.`;
});
byId("new-line").onclick = run(() => selectLine(null));
async function loadLines(before) {
  const request = ++linesGeneration;
  const query = new URLSearchParams({ q: byId("line-search").value.trim() });
  if (before != null) query.set("before", before);
  const page = await api(`${path()}/lines/page?${query}`);
  if (request !== linesGeneration) return;
  state.lines = before == null ? page.items : [...state.lines, ...page.items];
  linesCursor = page.nextBeforeId;
  listLinks("lines", state.lines, "Реплики не найдены.");
  byId("lines-more").hidden = linesCursor == null;
}
byId("line-search").oninput = () => {
  linesGeneration++;
  clearTimeout(linesTimer);
  linesTimer = setTimeout(run(() => loadLines()), 250);
};
byId("lines-more").onclick = run(() => loadLines(linesCursor));
byId("branches-more").onclick = run(async () => {
  if (branchesCursor == null) return;
  const page = await api(`${path()}/lines/page?branchesOnly=true&before=${branchesCursor}`);
  state.branches.push(...page.items);
  branchesCursor = page.nextBeforeId;
  listLinks("branches", state.branches, "Сюжетные развилки не отмечены.");
  byId("branches-more").hidden = branchesCursor == null;
});
lineForm.onsubmit = run(async () => {
  if (!drafts.confirmDiscard(lineForm)) return;
  const fields = lineForm.elements;
  const saved = await api(state.line ? linePath() : `${path()}/lines`, state.line ? "PUT" : "POST", {
    speakerId: Number(fields.speakerId.value),
    text: fields.text.value,
    isEnding: fields.isEnding.checked,
    isStoryBranch: fields.isStoryBranch.checked,
    version: state.line?.version
  });
  await loadDialogue(state.dialogue.id, saved?.id ?? state.line.id);
});
byId("set-start").onclick = run(async () => {
  if (!drafts.confirmDiscard()) return;
  await api(`${path()}/start`, "PUT", { lineId: state.line.id, version: state.dialogue.version });
  await loadDialogue(state.dialogue.id, state.line.id);
});
byId("clear-start").onclick = run(async () => {
  if (!drafts.confirmDiscard()) return;
  await api(`${path()}/start`, "PUT", { lineId: null, version: state.dialogue.version });
  await loadDialogue(state.dialogue.id, state.line.id);
});
byId("delete-line").onclick = run(async () => {
  if (!drafts.confirmDiscard()) return;
  if (window.confirm("Удалить реплику и её исходящие варианты?")) {
    await api(linePath(), "DELETE", { version: state.deleteVersion });
    await loadDialogue(state.dialogue.id);
  }
});
minimumLevelForm.onsubmit = run(async () => {
  if (!drafts.confirmDiscard(minimumLevelForm)) return;
  const field = minimumLevelForm.elements.minimumPlayerLevel;
  const minimum = field.value === "" ? null : field.valueAsNumber;
  await api(`${path()}/minimum-level`, "PUT", { minimumPlayerLevel: minimum, version: state.dialogue.version });
  await loadDialogue(state.dialogue.id, state.line?.id);
  byId("status").textContent = minimum == null ? "Ограничение по уровню снято." : "Минимальный уровень сохранён.";
});
byId("validate-dialogue").onclick = run(async () => {
  hideValidation();
  renderValidation(await api(`${path()}/validation`));
});
const refreshHistory = run(() => loadPreviewHistory());
byId("choices-first").onclick = run(() => selectLine(state.line.id));
byId("choices-more").onclick = run(async () => {
  if (contextPage?.nextOutgoingPosition == null) return;
  const page = await api(`${linePath()}/context?outgoingAfter=${contextPage.nextOutgoingPosition}&version=${contextPage.dialogue.version}`);
  for (let index = 0; index < page.outgoing.length; index++) {
    const form = choiceForm(page.outgoing[index], undefined, false,
      index === page.outgoing.length - 1 && page.nextOutgoingPosition == null);
    byId("choices").append(form);
    drafts.watch(form);
  }
  contextPage.nextOutgoingPosition = page.nextOutgoingPosition;
  byId("choices-more").hidden = page.nextOutgoingPosition == null;
});
byId("incoming-more").onclick = run(async () => {
  const cursor = contextPage?.nextIncoming;
  if (!cursor) return;
  const page = await api(`${linePath()}/context?incomingAfterLine=${cursor.sourceLineId}&incomingAfterPosition=${cursor.position}&version=${contextPage.dialogue.version}`);
  byId("incoming").append(...page.incoming.map(choice => linkToLine(choice.line.id, `${choice.text} ← ${shortLabel(choice.line)}`)));
  contextPage.nextIncoming = page.nextIncoming;
  byId("incoming-more").hidden = page.nextIncoming == null;
});
previewHistory.ontoggle = () => {
  if (previewHistory.open) refreshHistory();
};
byId("refresh-preview-history").onclick = refreshHistory;
previewSessions.onchange = run(() => showPreviewHistory(previewSessions.value));
byId("preview-sessions-more").onclick = run(async () => {
  if (!sessionCursor) return;
  const page = await api(`${path()}/previews/page?before=${sessionCursor}`);
  previewSessions.append(...page.items.map(previewSessionOption));
  sessionCursor = page.nextBeforeId;
  byId("preview-sessions-more").hidden = sessionCursor == null;
});
byId("preview-history-more").onclick = run(async () => {
  if (historyCursor == null || !previewSessions.value) return;
  const page = await api(`${path()}/previews/${previewSessions.value}/history?after=${historyCursor}`);
  byId("preview-history-steps").append(...page.steps.map(previewHistoryRow));
  historyCursor = page.nextAfterStep;
  byId("preview-history-more").hidden = historyCursor == null;
});

previewForm.onsubmit = run(async () => {
  byId("preview-step").replaceChildren();
  const level = previewForm.elements.playerLevel;
  await requestPreview(`${path()}/previews`, level.disabled ? undefined : { playerLevel: level.valueAsNumber });
});

await initializeWorkspace(openEditor);
