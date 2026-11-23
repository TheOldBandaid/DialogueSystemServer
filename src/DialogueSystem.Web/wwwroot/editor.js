const byId = id => document.getElementById(id);
const login = byId("login");
const workspace = byId("workspace");
const renameDialogueForm = byId("rename-dialogue");
const lineForm = byId("line-form");
const minimumLevelForm = byId("minimum-level");
const previewForm = byId("preview-form");
const previewHistory = byId("preview-history");
const previewSessions = byId("preview-sessions");
const historyDate = new Intl.DateTimeFormat("ru-RU", { dateStyle: "short", timeStyle: "medium" });
const accountAdmin = byId("account-admin");
const newAccountForm = byId("new-account");
const passwordForm = byId("change-password");
const roleLabels = { Editor: "Редактор", Administrator: "Администратор" };
const state = { dialogue: null, lines: [], characters: [], line: null, token: "" };

async function api(path, method = "GET", body) {
  const response = await fetch(path, {
    method,
    headers: method === "GET" ? {} : {
      "X-CSRF-TOKEN": state.token,
      "Content-Type": "application/json"
    },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const result = response.headers.get("content-type")?.includes("json") ? await response.json() : null;
  if (!response.ok) {
    const validation = result?.errors;
    const details = validation && Object.values(validation).flat().join(" ");
    const message = response.status === 401 ? "Проверьте имя пользователя и пароль или войдите заново." :
      result?.detail || details || `Запрос отклонён (HTTP ${response.status}).`;
    throw Object.assign(new Error(message), { status: response.status, result });
  }
  return result;
}

function run(operation) {
  return async event => {
    event?.preventDefault();
    workspace.inert = true;
    login.inert = true;
    byId("logout").disabled = true;
    byId("status").textContent = "";
    try {
      await operation(event);
    } catch (error) {
      if (error.status === 401) showLogin();
      byId("status").textContent = error.message;
    } finally {
      workspace.inert = false;
      login.inert = false;
      byId("logout").disabled = false;
    }
  };
}

async function refreshToken() {
  state.token = (await api("/api/accounts/csrf")).token;
}

function showLogin() {
  showAccountManagement(null);
  passwordForm.reset();
  byId("my-account").open = false;
  resetPreviewHistory();
  workspace.hidden = true;
  byId("logout").hidden = true;
  login.hidden = false;
}

function showAccountManagement(profile) {
  accountAdmin.hidden = profile?.role !== "Administrator";
  if (accountAdmin.hidden) {
    accountAdmin.open = false;
    newAccountForm.reset();
    byId("accounts-list").replaceChildren();
  }
}

async function accountRequest(route, method, body) {
  try {
    return await api(route, method, body);
  } catch (error) {
    if (error.status !== 403) throw error;
    showAccountManagement(null);
    throw new Error("Для управления ролями нужны права администратора.");
  }
}

function accountRow(account) {
  const row = document.createElement("li");
  const form = document.createElement("form");
  form.className = "actions account-role";
  const label = document.createElement("label");
  const loginName = document.createElement("span");
  loginName.textContent = account.login;
  const role = document.createElement("select");
  role.setAttribute("aria-label", `Роль для ${account.login}`);
  role.append(...Object.entries(roleLabels).map(([value, title]) => new Option(title, value)));
  role.value = account.role;
  label.append(loginName, role);
  const save = document.createElement("button");
  save.textContent = "Сохранить роль";
  save.disabled = true;
  role.onchange = () => { save.disabled = role.value === account.role; };

  form.onsubmit = run(async () => {
    await accountRequest(`/api/accounts/${account.id}/role`, "PUT", { role: role.value });
    showAccountManagement(await api("/api/accounts/me"));
    if (!accountAdmin.hidden) await loadAccounts();
    byId("status").textContent = `Роль пользователя «${account.login}» обновлена.`;
  });
  form.append(label, save);
  row.append(form);
  return row;
}

async function loadAccounts() {
  const accounts = await accountRequest("/api/accounts");
  byId("accounts-list").replaceChildren(...accounts.map(accountRow));
}

function setOptions(select, records, label, current) {
  select.replaceChildren(...records.map(item => new Option(label(item), item.id)));
  if (current != null) select.value = current;
}

const path = () => `/api/dialogues/${state.dialogue.id}`;
const linePath = () => `${path()}/lines/${state.line.id}`;
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
  renameDialogueForm.elements.title.value = dialogue.title;
  renderLevelSettings();
  state.lines = lines;
  listLinks("lines", lines, "Реплик пока нет.");
  listLinks("branches", branches, "Сюжетные развилки не отмечены.");
  await selectLine(preferredLineId ?? branches[0]?.id ?? dialogue.startLineId ?? lines[0]?.id);
}

async function selectLine(id) {
  const context = id == null ? null : await api(`${path()}/lines/${id}/context`);
  state.line = context?.line ?? null;
  const line = state.line;
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
  });
  return form;
}

async function openWorkspace(preferredDialogueId, preferredLineId) {
  const [dialogues, characters] = await Promise.all([api("/api/dialogues"), api("/api/characters")]);
  state.characters = characters;
  const select = byId("dialogues");
  const preferred = dialogues.find(dialogue => dialogue.id === preferredDialogueId);
  setOptions(select, dialogues, dialogue => dialogue.title, preferred?.id);
  select.disabled = dialogues.length === 0;
  login.hidden = true;
  workspace.hidden = false;
  byId("logout").hidden = false;
  byId("dialogue").hidden = dialogues.length === 0;
  byId("preview").hidden = dialogues.length === 0;
  if (dialogues.length === 0) {
    state.dialogue = null;
    state.lines = [];
    state.line = null;
    previewForm.reset();
    resetPreviewHistory();
    byId("status").textContent = "Создайте диалог, чтобы начать редактирование.";
    return;
  }
  await loadDialogue(Number(select.value), preferred ? preferredLineId : undefined);
}

async function importDialogue(data) {
  const created = await api("/api/dialogues/import", "POST", data);
  byId("preview-step").replaceChildren();
  await openWorkspace(created.id);
  return created;
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

const refreshAccounts = run(loadAccounts);
accountAdmin.ontoggle = () => {
  if (accountAdmin.open && !accountAdmin.hidden) refreshAccounts();
};
byId("refresh-accounts").onclick = refreshAccounts;
newAccountForm.onsubmit = run(async () => {
  const input = Object.fromEntries(new FormData(newAccountForm));
  const account = await accountRequest("/api/accounts", "POST", input);
  newAccountForm.reset();
  await loadAccounts();
  byId("status").textContent = `Пользователь «${account.login}» создан.`;
});
passwordForm.onsubmit = run(async () => {
  const { currentPassword, newPassword, confirmation } = Object.fromEntries(new FormData(passwordForm));
  if (newPassword !== confirmation) throw new Error("Новые пароли не совпадают.");

  await api("/api/accounts/me/password", "PUT", { currentPassword, newPassword });
  byId("preview-step").replaceChildren();
  showLogin();
  byId("status").textContent = "Пароль изменён. Войдите с новым паролем.";
});

login.onsubmit = run(async () => {
  await refreshToken();
  const profile = await api("/api/accounts/login", "POST", Object.fromEntries(new FormData(login)));
  login.reset();
  await refreshToken();
  showAccountManagement(profile);
  await openWorkspace();
});
byId("logout").onclick = run(async () => {
  await api("/api/accounts/logout", "POST");
  byId("preview-step").replaceChildren();
  showLogin();
});
byId("dialogues").onchange = run(async event => {
  byId("preview-step").replaceChildren();
  await loadDialogue(Number(event.target.value));
});
byId("new-dialogue").onsubmit = run(async event => {
  const form = event.currentTarget;
  const dialogue = await api("/api/dialogues", "POST", Object.fromEntries(new FormData(form)));
  form.reset();
  byId("preview-step").replaceChildren();
  await openWorkspace(dialogue.id);
});
renameDialogueForm.onsubmit = run(async () => {
  const id = state.dialogue.id;
  await api(path(), "PUT", { title: renameDialogueForm.elements.title.value });
  await openWorkspace(id, state.line?.id);
  if (state.dialogue?.id === id) byId("status").textContent = `Диалог переименован в «${state.dialogue.title}».`;
});
byId("delete-dialogue").onclick = run(async () => {
  const title = state.dialogue.title;
  if (!window.confirm(`Удалить диалог «${title}», его реплики, переходы и историю прохождений?`)) return;

  await api(path(), "DELETE");
  byId("preview-step").replaceChildren();
  await openWorkspace();
  if (state.dialogue) byId("status").textContent = `Диалог «${title}» удалён.`;
});
byId("import-dialogue").onsubmit = run(async event => {
  const form = event.currentTarget;
  const raw = await form.elements.file.files[0].text();
  let data;
  try {
    data = JSON.parse(raw.replace(/^\uFEFF/, ""));
  } catch (error) {
    if (!(error instanceof SyntaxError)) throw error;
    throw new Error("Не удалось прочитать JSON. Проверьте содержимое файла.");
  }
  const created = await importDialogue(data);
  form.reset();
  byId("status").textContent = `Диалог «${created.title}» загружен.`;
});
byId("copy-dialogue").onclick = run(async () => {
  const data = await api(`${path()}/export`);
  const suffix = " (копия)";
  const title = data.title.length <= 160 - suffix.length ? data.title + suffix : "Копия диалога";
  const created = await importDialogue({ ...data, title });
  byId("status").textContent = `Создана копия «${created.title}».`;
});
byId("export-dialogue").onclick = run(async () => {
  const id = state.dialogue.id;
  const data = await api(`${path()}/export`);
  const file = new Blob([JSON.stringify(data, null, 2)], { type: "application/json" });
  const url = URL.createObjectURL(file);
  const link = document.createElement("a");
  link.href = url;
  link.download = `dialogue-${id}.json`;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 0);
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

await run(async () => {
  try {
    showAccountManagement(await api("/api/accounts/me"));
  } catch (error) {
    if (error.status !== 401) throw error;
    showLogin();
    return;
  }
  await refreshToken();
  await openWorkspace();
})();
