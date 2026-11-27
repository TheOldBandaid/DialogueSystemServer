import { api } from "./http-client.js";
import { byId, run } from "./ui.js";

export function initializePreview(path, renderValidation) {
  const previewForm = byId("preview-form");
  const previewHistory = byId("preview-history");
  const previewSessions = byId("preview-sessions");
  const historyDate = new Intl.DateTimeFormat("ru-RU", { dateStyle: "short", timeStyle: "medium" });
  let sessionCursor;
  let historyCursor;

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
    const page = await api(`${path()}/previews`);
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

  const refreshHistory = run(() => loadPreviewHistory());
  previewHistory.ontoggle = () => {
    if (previewHistory.open) refreshHistory();
  };
  byId("refresh-preview-history").onclick = refreshHistory;
  previewSessions.onchange = run(() => showPreviewHistory(previewSessions.value));
  byId("preview-sessions-more").onclick = run(async () => {
    if (!sessionCursor) return;
    let page;
    try { page = await api(`${path()}/previews?before=${sessionCursor}`); }
    catch (error) {
      if (error.status !== 404) throw error;
      await loadPreviewHistory(null);
      byId("preview-history-message").textContent = "Список прохождений изменился. Загружена первая страница.";
      return;
    }
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


  function setMinimumLevel(minimum) {
    const hasMinimum = minimum != null;
    byId("preview-level").hidden = !hasMinimum;
    byId("preview-level-caption").textContent = hasMinimum ? `Уровень игрока (от ${minimum})` : "Уровень игрока";
    previewForm.elements.playerLevel.disabled = !hasMinimum;
    previewForm.elements.playerLevel.required = hasMinimum;
  }

  function reset() {
    previewForm.reset();
    byId("preview-step").replaceChildren();
    resetPreviewHistory();
  }
  return { reset, resetHistory: resetPreviewHistory, setMinimumLevel };
}
