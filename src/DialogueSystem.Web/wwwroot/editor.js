const byId = id => document.getElementById(id);
const login = byId("login");
const workspace = byId("workspace");
const lineForm = byId("line-form");
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
    throw Object.assign(new Error(message), { status: response.status });
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
  workspace.hidden = true;
  byId("logout").hidden = true;
  login.hidden = false;
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

function linkToLine(line, label) {
  const button = document.createElement("button");
  button.type = "button";
  button.textContent = label;
  button.onclick = run(() => selectLine(line.id));
  const item = document.createElement("li");
  item.append(button);
  return item;
}

function listLinks(target, lines, empty) {
  const list = byId(target);
  list.replaceChildren(...lines.map(line => linkToLine(line, shortLabel(line))));
  if (lines.length === 0) {
    const item = document.createElement("li");
    item.textContent = empty;
    list.append(item);
  }
}

async function loadDialogue(id, preferredLineId) {
  const route = `/api/dialogues/${id}`;
  const [dialogue, lines, branches] = await Promise.all([
    api(route), api(`${route}/lines`), api(`${route}/story-branches`)
  ]);
  state.dialogue = dialogue;
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
    linkToLine(choice.line, `${choice.text} ← ${shortLabel(choice.line)}`)));
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
    <div class="actions">
      <button>Сохранить вариант</button>
      <button type="button" name="target">Открыть цель</button>
      <button type="button" name="remove"></button>
    </div>`;
  const fields = form.elements;
  fields.text.value = choice?.text ?? "";
  fields.position.value = choice?.position ?? position;
  setOptions(fields.targetLineId, state.lines, shortLabel, choice?.line.id);
  fields.target.hidden = !choice;
  fields.target.onclick = run(() => selectLine(choice.line.id));
  fields.remove.textContent = choice ? "Удалить вариант" : "Отмена";
  fields.remove.onclick = run(async () => {
    if (choice) {
      await api(`${linePath()}/choices/${choice.id}`, "DELETE");
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
      position: Number(fields.position.value)
    });
    await selectLine(state.line.id);
  });
  return form;
}

async function openWorkspace(preferredDialogueId) {
  const [dialogues, characters] = await Promise.all([api("/api/dialogues"), api("/api/characters")]);
  state.characters = characters;
  setOptions(byId("dialogues"), dialogues, dialogue => dialogue.title, preferredDialogueId);
  login.hidden = true;
  workspace.hidden = false;
  byId("logout").hidden = false;
  byId("dialogue").hidden = dialogues.length === 0;
  byId("preview").hidden = dialogues.length === 0;
  if (dialogues.length === 0) {
    byId("status").textContent = "Создайте диалог, чтобы начать редактирование.";
    return;
  }
  await loadDialogue(Number(byId("dialogues").value));
}

function renderStep(step) {
  const host = byId("preview-step");
  const text = document.createElement("p");
  text.textContent = `${step.line.speaker} — ${step.line.text}${step.line.isEnding ? " · Конец диалога" : ""}`;
  const actions = document.createElement("div");
  actions.className = "actions";
  for (const choice of step.choices) {
    const button = document.createElement("button");
    button.textContent = choice.text;
    button.onclick = run(async () => {
      renderStep(await api(`${path()}/previews/${step.previewId}/choices`, "POST", {
        choiceId: choice.id, step: step.step
      }));
    });
    actions.append(button);
  }
  host.replaceChildren(text, actions);
}

login.onsubmit = run(async () => {
  await refreshToken();
  await api("/api/accounts/login", "POST", Object.fromEntries(new FormData(login)));
  login.reset();
  await refreshToken();
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
byId("start-preview").onclick = run(async () => {
  byId("preview-step").replaceChildren();
  renderStep(await api(`${path()}/previews`, "POST"));
});

await run(async () => {
  try {
    await api("/api/accounts/me");
  } catch (error) {
    if (error.status !== 401) throw error;
    showLogin();
    return;
  }
  await refreshToken();
  await openWorkspace();
})();
